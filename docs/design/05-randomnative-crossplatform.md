# 05 · DAFLegacy.RandomNative 跨平台化提案（已作废）

> **本提案已作废，不要据此实施。**
>
> 本文研究的是"如何把 `DAFLegacy.RandomNative` 改造成可跨平台编译"。后续决定服务端随机源降级为纯托管的单一实现，并与该模块解耦；**模块本身保留至 `Client/DAFLegacy.RandomNative/`，不跨平台移植**。取代本文的是 [06 · 随机源降级为纯托管实现](06-random-source-managed.md)。
>
> 保留本文仅作技术记录，其中三项实测结论仍然有效：`_rdseed64_step` 需要目标特性且三个编译器处理方式不同、clang 没有 `__builtin_ia32_rdseed_di_step`、以及 CTest 用例在 Release 下因 `NDEBUG` 而空跑。另有 §5.6 关于 `.vcxproj` 与 `MSB4278` 的实测记录。
>
> 本文的交叉引用指向写作时存在的小节（`01 · 4.4`、`03 · 4.2` / `4.4`、`04 · M6` 等）。这些小节已随服务端与该模块的解耦一并移除，本文中指向它们的链接因此失效——这是**刻意保留的历史痕迹，不再回改**；需要当前结论时请读 06。

- 状态：**已作废**（被 [06](06-random-source-managed.md) 取代；后继方案 06 已于 2026-09-24 标注为已实施）
- 上级文档：[00 开档总纲](00-kickoff.md) · [01 目标架构](01-target-architecture.md) · [02 可移植性契约](02-portability-contracts.md) · [03 构建、发布与 CI](03-build-release-ci.md)
- 原对应里程碑：M6（可选）——**已取消**
- 验证环境：macOS 26.6.2 / Apple clang 21.0.0 / dotnet SDK 10.0.301 / 无 cmake、无 GCC、无 MSVC

本文回答一个问题：**`DAFLegacy.RandomNative` 能否通过手工改造实现跨平台编译。** 结论是能，且本文给出经过编译验证的完整提案源码与测试代码。**在批准之前，本文所有代码都只作为设计材料存放，不写入 `src/`。**

---

## 1. 范围

**在范围内**：让 `DAFLegacy.RandomNative` 在 `win-x64` / `linux-x64` / `linux-arm64` / `macos-arm64` 上都能编译，且**接口与 ABI 完全不变**（两个导出符号、参数与返回值语义一致）。

**明确不在范围内**（都是独立的事，不要混进本次改造）：

| 事项 | 为什么不在范围内 |
| --- | --- |
| 托管侧按 RID 探测 + `SetDllImportResolver` | 属 [02 · R2](02-portability-contracts.md#r2--原生能力的运行期发现) / M2。**只做本提案，Linux 上仍不会加载原生桥**——因为 `GameRandomSource.cs` 的 `CpuAdvertisesRdSeed()` 第一条判断是 `OperatingSystem.IsWindows()`（`GameRandomSource.cs:198`） |
| 进入 `.slnx` 解决方案 | 与本次无关，且不可能：`.vcxproj` 在 `dotnet` CLI 上恒报 `MSB4278`（实测见 §5.6） |
| 产物目录改 RID 布局（`artifacts/native/random/<config>/<rid>/`） | 属 M2。改动会破坏 `DAFLegacy.Server.csproj:8` 现有的消费路径，本提案保持 Windows 产物位置不变以免引入回归 |
| 托管侧 `IRandomSource` 抽象 | 属 M2，与本提案正交 |

---

## 2. 现状阻塞点

[01 · 4.4](01-target-architecture.md#44-daflegacyrandomnative服务端可选原生资产可选跨平台化) 已列出七条。本文保留该清单，并在 §3 补上本次实测发现的、清单里没有或写错的部分。

| 阻塞点 | 位置 | 处理 |
| --- | --- | --- |
| CMake 对非 `WIN32` 直接 `FATAL_ERROR` | `CMakeLists.txt:5-7` | 移除，改按目标架构判定 |
| `CMAKE_SIZEOF_VOID_P EQUAL 8` 判 x64 | `CMakeLists.txt:9-11` | 该判据在 ARM64 上同样为真；改查 `CMAKE_SYSTEM_PROCESSOR` |
| MSVC 专属选项 `/W4 /WX /permissive-` | `CMakeLists.txt:26-28` | 按编译器分支 |
| MSVC 专属内建 `__cpuidex` / `_rdseed64_step` / `_mm_pause` | `daflegacy_random.cpp:12,18,43,49` | 按编译器与架构分支 |
| `__declspec(dllexport/dllimport)` | `daflegacy_random.h:5-11` | 非 Windows 走 `visibility("default")` |
| `__cdecl` | 头文件与实现 | 定义统一宏；x86-64 上本就只有一种约定 |
| `.def` 文件 | `src/daflegacy_random.def` | Windows 保留；其他平台靠导出宏 |
| `build-native.ps1` 要求 VS 生成器 | `build-native.ps1` | 不动它（Windows 入口保持现状），另加跨平台 CMake 预设 |

---

## 3. 本次实测新增的三项发现

### 3.1 `_rdseed64_step` 需要目标特性，且三个编译器的处理方式互不相同

这是 [01 · 4.4](01-target-architecture.md#44-daflegacyrandomnative服务端可选原生资产可选跨平台化) 与 [03 · 4.4](03-build-release-ci.md#44-daflegacyrandomnative-的跨平台化m6可选) 都没有记录的细节，也是这次改造最容易踩的坑。

实测：clang 在不加任何标注时直接编译失败。

```text
error: always_inline function '_rdseed64_step' requires target feature 'rdseed',
       but would be inlined into function 'daflegacy_rdseed64'
       that is compiled without support for 'rdseed'
```

三个编译器的实际行为（每一条都有依据）：

| 编译器 | 取得 RDSEED 能力的方式 | 依据 |
| --- | --- | --- |
| clang | **调用方必须标注**：`__attribute__((target("rdseed")))` 或 TU 级 `-mrdseed`。clang 自带的 `<immintrin.h>` 不启用该特性 | 本机实测：无标注报错，加属性或 `-mrdseed` 均可编译 |
| GCC | **什么都不需要**。GCC 自己的 `rdseedintrin.h` 在 `__RDSEED__` 守卫内用 `#pragma GCC target("rdseed")` 包裹了定义 | 读 GCC 源码 `gcc/config/i386/rdseedintrin.h`：文件不以 pragma 开头，pragma 出现在 `#ifdef __RDSEED__` 区域内 |
| MSVC | **什么都不需要**。`_rdseed64_step` 是始终可用的内建 | 现有 Windows 实现即无任何相关开关，本提案原样保留 |

**结论：提案不使用 TU 级 `-mrdseed`。** 理由是函数级标注更精确——TU 级标志会把整个翻译单元（含两个导出入口）的基线 ISA 抬到含 RDSEED，而实际只有一处需要；同时源码自足意味着 `build-native.ps1` 的 MSVC 路径、CMake 路径、以及任何手工 `clang++` 调用都无需记得加标志。仅 clang 分支需要该属性。

### 3.2 `__builtin_ia32_rdseed_di_step` 是 GCC 专有，clang 没有

[01 · 4.4](01-target-architecture.md#44-daflegacyrandomnative服务端可选原生资产可选跨平台化) 与 [03 · 4.4](03-build-release-ci.md#44-daflegacyrandomnative-的跨平台化m6可选) 都写着「Clang/GCC 走 `__cpuid_count` / `__builtin_ia32_rdseed_di_step` / `__builtin_ia32_pause`」。**这一条对 clang 不成立**：

```text
Apple clang version 21.0.0
error: use of undeclared identifier '__builtin_ia32_rdseed_di_step'
```

clang 提供的是 Intel 风格的内建：`_rdseed64_step`（来自 `<immintrin.h>`）。GCC 两者都有——GCC 的 `_rdseed64_step` 本身就是 `__builtin_ia32_rdseed_di_step` 的包装。所以正确的写法是：clang 与 MSVC 用 `_rdseed64_step`，GCC 用 `__builtin_ia32_rdseed_di_step`；`_mm_pause()` 三方都可用，可统一（注意 clang 的 `__builtin_ia32_pause()` 返回 `void`，直接当 `int` 用会编译失败）。

**建议同步修正那两处文档**，否则实施者照抄会在 clang 上直接编译失败。

### 3.3 现有 CTest 用例在 Release 下是空跑

`tests/random_native_tests.cpp` 全部用 `assert`，而 `CMAKE_BUILD_TYPE=Release` 与 vcxproj 的 Release 配置都会定义 `NDEBUG`——**所有断言被预处理掉，测试恒返回 0**。这意味着 [03 · 5.1](03-build-release-ci.md#51-buildyml-目标结构) 里 `windows-full` job 补的那步 `-RunTests`、以及 [M6 验收](04-migration-plan.md#m6--原生可移植可选) 里的 CTest 步骤，在当前实现下不验证任何东西。

提案把测试改为显式运行期检查 + 非零退出码（§4.5），顺带补上两个此前没覆盖的契约：`retry_count = 0` 按 1 次处理、空指针必须拒绝而不是解引用。

---

## 4. 提案实现

五个文件。前三份是 `src/DAFLegacy.RandomNative/` 下现有文件的替换内容，第四份是新增，第五份是测试。

### 4.1 `include/daflegacy_random.h`

```cpp
#pragma once

#include <cstdint>

// Calling convention. It only means something on 32-bit x86; on x86-64 there is
// a single integer convention. Kept explicit so the managed side's
// CallingConvention.Cdecl remains an exact match on every platform.
#if defined(_WIN32)
#define DAFLEGACY_RANDOM_CALL __cdecl
#else
#define DAFLEGACY_RANDOM_CALL
#endif

// Export/import. Windows uses __declspec and pins the symbol names with a .def
// file; everywhere else the symbols are kept by this macro plus the build's
// hidden default visibility.
#if defined(_WIN32)
#if defined(DAFLEGACY_RANDOM_EXPORTS)
#define DAFLEGACY_RANDOM_API __declspec(dllexport)
#else
#define DAFLEGACY_RANDOM_API __declspec(dllimport)
#endif
#elif defined(__GNUC__) || defined(__clang__)
#define DAFLEGACY_RANDOM_API __attribute__((visibility("default")))
#else
#define DAFLEGACY_RANDOM_API
#endif

extern "C"
{
    // 1 when this process can use the RDSEED hardware entropy source, 0 when it
    // cannot and the caller has to fall back. Always 0 on architectures without
    // RDSEED, ARM64 included, so both symbols exist everywhere and the host
    // never has to branch on the architecture or the operating system.
    DAFLEGACY_RANDOM_API int DAFLEGACY_RANDOM_CALL daflegacy_rdseed_supported() noexcept;

    // Writes one 64-bit RDSEED sample to *value and returns 1; returns 0 after
    // retry_count unsuccessful attempts, leaving *value untouched. A retry_count
    // of 0 is treated as 1.
    DAFLEGACY_RANDOM_API int DAFLEGACY_RANDOM_CALL daflegacy_rdseed64(
        std::uint32_t retry_count,
        std::uint64_t* value) noexcept;
}
```

**接口未变**：两个符号名、参数类型、`noexcept`、返回码语义全部与现版一致。这也是托管侧 `DllImport` 声明（`GameRandomSource.cs:231,240`）不需要跟着改的原因。

### 4.2 `src/daflegacy_random.cpp`

```cpp
// DAFLegacy.RandomNative - the server's optional RDSEED entropy bridge.
//
// Portability notes:
//   * The interface is architecture independent. On anything that is not x86-64,
//     ARM64 included, this file builds the always-zero stub: both symbols stay
//     present, so the host probes and downgrades at run time instead of
//     branching on the architecture or the operating system.
//   * Capability is decided at run time from CPUID, so one binary serves CPUs
//     with and without RDSEED. An unavailable capability is a downgrade, not an
//     error, and no code path faults on a CPU without the instruction.
//   * Compiler and architecture differences are confined to this file and are
//     not pushed onto the build: no translation-unit-wide ISA flag (-mrdseed,
//     /arch) is required on any of the three compilers.

#include "daflegacy_random.h"

#include <algorithm>
#include <cstdint>

#if defined(__x86_64__) || defined(__amd64__) || defined(_M_X64)
#define DAFLEGACY_RANDOM_ARCH_X86_64 1
#endif

#if defined(DAFLEGACY_RANDOM_ARCH_X86_64)
#if defined(_MSC_VER)
#include <immintrin.h>
#include <intrin.h>
#define DAFLEGACY_RDSEED_STEP(value) (_rdseed64_step(value) != 0)
#else
#include <cpuid.h>
#include <immintrin.h>
#if defined(__clang__)
// clang ships the Intel-style intrinsic, and its header does not enable the
// target feature, so the caller has to: without an explicit target annotation
// _rdseed64_step fails with "requires target feature 'rdseed'".
#define DAFLEGACY_RDSEED_STEP(value) (_rdseed64_step(value) != 0)
#define DAFLEGACY_RDSEED_TARGET __attribute__((target("rdseed")))
#else
// GCC needs no annotation: its own rdseedintrin.h wraps the definitions in
// "#pragma GCC target("rdseed")" guarded by __RDSEED__, so _rdseed64_step is
// callable from a translation unit built without -mrdseed.
#define DAFLEGACY_RDSEED_STEP(value) (__builtin_ia32_rdseed_di_step(value) != 0)
#endif
#endif
#endif

#if !defined(DAFLEGACY_RDSEED_TARGET)
#define DAFLEGACY_RDSEED_TARGET
#endif

#if defined(DAFLEGACY_RANDOM_ARCH_X86_64)

namespace
{
    // CPUID leaf 7, EBX bit 18 reports RDSEED. Probing the feature bit is
    // preferred over executing the instruction and catching the result, which is
    // not reliably observable on every platform.
    bool detect_rdseed() noexcept
    {
#if defined(_MSC_VER)
        int registers[4]{};
        __cpuidex(registers, 0, 0);
        if (registers[0] < 7)
        {
            return false;
        }

        __cpuidex(registers, 7, 0);
        const auto feature_bits = static_cast<unsigned int>(registers[1]);
#else
        unsigned int registers[4]{};
        __cpuid_count(0, 0, registers[0], registers[1], registers[2], registers[3]);
        if (registers[0] < 7u)
        {
            return false;
        }

        __cpuid_count(7, 0, registers[0], registers[1], registers[2], registers[3]);
        const unsigned int feature_bits = registers[1];
#endif
        constexpr unsigned int rdseed_bit = 1u << 18;
        return (feature_bits & rdseed_bit) != 0;
    }

    // The only place the RDSEED instruction is emitted. Kept in its own function
    // so the target feature cannot spread into the exported entry points.
    DAFLEGACY_RDSEED_TARGET bool try_read_sample(std::uint64_t* const value) noexcept
    {
        unsigned long long candidate = 0;
        if (!DAFLEGACY_RDSEED_STEP(&candidate))
        {
            return false;
        }

        *value = candidate;
        return true;
    }
}

#endif

extern "C" DAFLEGACY_RANDOM_API int DAFLEGACY_RANDOM_CALL
daflegacy_rdseed_supported() noexcept
{
#if defined(DAFLEGACY_RANDOM_ARCH_X86_64)
    // CPUID is constant for the lifetime of the process, so probe once.
    static const bool supported = detect_rdseed();
    return supported ? 1 : 0;
#else
    return 0;
#endif
}

extern "C" DAFLEGACY_RANDOM_API int DAFLEGACY_RANDOM_CALL
daflegacy_rdseed64(const std::uint32_t retry_count, std::uint64_t* const value) noexcept
{
    if (value == nullptr || daflegacy_rdseed_supported() == 0)
    {
        return 0;
    }

#if defined(DAFLEGACY_RANDOM_ARCH_X86_64)
    const auto attempts = std::max<std::uint32_t>(1, retry_count);
    for (std::uint32_t attempt = 0; attempt < attempts; ++attempt)
    {
        if (try_read_sample(value))
        {
            return 1;
        }

        _mm_pause();
    }
#else
    (void)retry_count;
#endif

    return 0;
}
```

相对现版的改动只有三处，Windows 行为逐位保持：`unsigned __int64` 换成可移植的 `unsigned long long`、MSVC 分支的 CPUID 判定与取值原样保留、其余全部是 `#if` 分支与结构拆分。

### 4.3 `CMakeLists.txt`

```cmake
cmake_minimum_required(VERSION 3.24)

project(DAFLegacy.RandomNative LANGUAGES CXX)

# RDSEED is an x86-64 ISA feature; it does not exist on ARM64. The target
# architecture is therefore what decides between the real implementation and the
# stub, on every platform.
#
# The architecture must NOT be detected with CMAKE_SIZEOF_VOID_P EQUAL 8: that
# is also true on 64-bit ARM, which is exactly how an ARM64 host would end up
# with a library that cannot work. (The Visual Studio generator reports AMD64.)
if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|AMD64|amd64)$")
    set(DAFLEGACY_RANDOM_ARCH "x86_64")
else()
    set(DAFLEGACY_RANDOM_ARCH "stub")
    message(STATUS
        "DAFLegacy.RandomNative: ${CMAKE_SYSTEM_PROCESSOR} has no RDSEED, "
        "building the always-zero stub. Both symbols stay present, so the host "
        "probes and downgrades at run time instead of branching on the "
        "architecture.")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(MSVC)
    # Static runtime, matching the historical Windows build.
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

add_library(DAFLegacy.RandomNative SHARED
    include/daflegacy_random.h
    src/daflegacy_random.cpp)

if(WIN32)
    # The .def pins the exported symbol names on Windows. Elsewhere the export
    # macro plus the hidden default visibility below do the same job.
    target_sources(DAFLegacy.RandomNative PRIVATE src/daflegacy_random.def)
endif()

target_compile_definitions(DAFLegacy.RandomNative PRIVATE
    DAFLEGACY_RANDOM_EXPORTS)

if(MSVC)
    target_compile_options(DAFLegacy.RandomNative PRIVATE
        /W4
        /WX
        /permissive-)
else()
    target_compile_options(DAFLegacy.RandomNative PRIVATE
        -Wall
        -Wextra
        -Werror)
endif()

target_include_directories(DAFLegacy.RandomNative PUBLIC include)

# OUTPUT_NAME holds the base name steady across platforms; CMake's default
# PREFIX then produces DAFLegacy.RandomNative.dll on Windows and
# libDAFLegacy.RandomNative.so / .dylib elsewhere, which is the naming the
# runtimes/<rid>/native/ layout expects.
set_target_properties(DAFLegacy.RandomNative PROPERTIES
    OUTPUT_NAME "DAFLegacy.RandomNative"
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES
    DEBUG_POSTFIX "")

include(CTest)
if(BUILD_TESTING)
    add_executable(DAFLegacy.RandomNative.Tests
        tests/random_native_tests.cpp)
    if(MSVC)
        target_compile_options(DAFLegacy.RandomNative.Tests PRIVATE
            /W4
            /WX
            /permissive-)
    else()
        target_compile_options(DAFLegacy.RandomNative.Tests PRIVATE
            -Wall
            -Wextra
            -Werror)
    endif()
    target_include_directories(DAFLegacy.RandomNative.Tests PRIVATE include)
    target_link_libraries(DAFLegacy.RandomNative.Tests PRIVATE DAFLegacy.RandomNative)
    add_test(NAME rdseed_native COMMAND DAFLegacy.RandomNative.Tests)
endif()
```

三点说明：

- 默认 `PREFIX` 恰好产出设计要的文件名：Windows 无前缀 `DAFLegacy.RandomNative.dll`，Linux `libDAFLegacy.RandomNative.so`，macOS `libDAFLegacy.RandomNative.dylib`——与 [02 · R2](02-portability-contracts.md#r2--原生能力的运行期发现) 的 RID 布局约定一致，无需额外设置。
- `CXX_VISIBILITY_PRESET hidden` 是必需项：没有它，全部符号（含 `detect_rdseed` 等内部符号）都会导出。实测加上之后导出符号恰好 2 个（§5.3）。
- Windows 的 `CMAKE_MSVC_RUNTIME_LIBRARY`（静态 CRT）必须保留，且必须在 `add_library` 之前设置才有意义。

### 4.4 `CMakePresets.json`（新增）

预设置于项目目录内。**不含 Windows 预设**：Windows 入口继续由 `build-native.ps1` 承担（它会按已安装的 VS 版本自动挑选生成器并做缓存校验，比固定生成器更稳），本次不给 Windows 引入第二条不太健壮的入口。

```json
{
  "version": 5,
  "cmakeMinimumRequired": {
    "major": 3,
    "minor": 24,
    "patch": 0
  },
  "configurePresets": [
    {
      "name": "linux-x64",
      "displayName": "Linux x64 (real RDSEED implementation)",
      "description": "Produces libDAFLegacy.RandomNative.so with the RDSEED backend.",
      "binaryDir": "${sourceDir}/../../artifacts/native/random/linux-x64",
      "cacheVariables": {
        "CMAKE_BUILD_TYPE": "Release"
      }
    },
    {
      "name": "linux-arm64",
      "displayName": "Linux ARM64 (always-zero stub)",
      "description": "No RDSEED on ARM64. Both symbols are still exported and every call reports no sample.",
      "binaryDir": "${sourceDir}/../../artifacts/native/random/linux-arm64",
      "cacheVariables": {
        "CMAKE_BUILD_TYPE": "Release"
      }
    },
    {
      "name": "macos-arm64",
      "displayName": "macOS ARM64 (development host, always-zero stub)",
      "description": "Development host only; not a release target. Produces a stub build.",
      "binaryDir": "${sourceDir}/../../artifacts/native/random/macos-arm64",
      "cacheVariables": {
        "CMAKE_BUILD_TYPE": "Release"
      }
    }
  ],
  "buildPresets": [
    {
      "name": "linux-x64-release",
      "configurePreset": "linux-x64"
    },
    {
      "name": "linux-arm64-release",
      "configurePreset": "linux-arm64"
    },
    {
      "name": "macos-arm64-release",
      "configurePreset": "macos-arm64"
    }
  ],
  "testPresets": [
    {
      "name": "linux-x64-release",
      "configurePreset": "linux-x64",
      "output": {
        "outputOnFailure": true
      }
    },
    {
      "name": "macos-arm64-release",
      "configurePreset": "macos-arm64",
      "output": {
        "outputOnFailure": true
      }
    }
  ]
}
```

`${sourceDir}/../../` 依赖"项目目录距仓库根两层"这个前提。这与 [01 · 10.1](01-target-architecture.md#101-完整影响清单) 的"`Server/` 与 `src/` 深度相同"是同一条性质，**M0 把目录移到 `Server/DAFLegacy.RandomNative/` 后这些路径依旧成立**；但若目录嵌进 `DAFLegacy.Server/` 之下（深度变 3），全部失效。

### 4.5 `tests/random_native_tests.cpp`

```cpp
// CTest entry point for the RDSEED bridge: rdseed_native.
//
// Deliberately not <cassert>. The Release configuration defines NDEBUG, which
// would compile every assert away and leave this test verifying nothing while
// still exiting 0. The checks below are ordinary runtime checks so the test
// behaves identically in Debug and Release.
//
// The test must also pass on the architectures where the library is the
// always-zero stub: the interface is expected to stay present and to report
// "no sample" instead of failing to load.

#include "daflegacy_random.h"

#include <cstdint>
#include <cstdio>

namespace
{
    int failure_count = 0;

    void check(const bool condition, const char* const message)
    {
        if (!condition)
        {
            std::fprintf(stderr, "FAIL: %s\n", message);
            ++failure_count;
        }
    }
}

int main()
{
    const auto supported = daflegacy_rdseed_supported();
    check(supported == 0 || supported == 1, "capability query returns 0 or 1");

    // A retry count of 0 has to mean one attempt, not "give up immediately".
    // Whether that attempt yields a sample depends on the CPU, so only the
    // return-code contract can be asserted here.
    std::uint64_t value = 0;
    const auto zero_retry = daflegacy_rdseed64(0, &value);
    check(zero_retry == 0 || zero_retry == 1, "zero retry count returns 0 or 1");

    // A null destination must be refused rather than dereferenced.
    check(daflegacy_rdseed64(1, nullptr) == 0, "null destination returns 0");

    if (supported == 0)
    {
        // Stub build, or a CPU without RDSEED. The call has to report "no
        // sample" and leave the destination untouched, because the host reads
        // the return code and must not see a stale-looking value.
        constexpr std::uint64_t sentinel = 0x5A5A5A5A5A5A5A5Aull;
        value = sentinel;
        check(daflegacy_rdseed64(8, &value) == 0, "no sample is reported without RDSEED");
        check(value == sentinel, "the destination is left untouched without RDSEED");

        std::printf(
            "DAFLegacy.RandomNative: RDSEED unavailable, stub contract verified, "
            "%d failure(s).\n",
            failure_count);
        return failure_count == 0 ? 0 : 1;
    }

    bool generated = false;
    for (int attempt = 0; attempt < 100 && !generated; ++attempt)
    {
        generated = daflegacy_rdseed64(8, &value) != 0;
    }

    check(generated, "a sample is produced within 100 attempts when RDSEED is available");

    std::printf(
        "DAFLegacy.RandomNative: RDSEED available, %d failure(s).\n",
        failure_count);
    return failure_count == 0 ? 0 : 1;
}
```

**未改动**：`DAFLegacy.RandomNative.vcxproj` 与 `build-native.ps1`。前者列出的源文件集合（头文件、`.cpp`、`.def`、测试）没有变化，MSVC 路径也不需要任何新开关（§3.1），因此无需同步；后者是 Windows 入口，本次保持原状。唯一可选的小同步是把新增的 `CMakePresets.json` 加进 vcxproj 的 `<None>` 项（现有文件已把 `CMakeLists.txt` 与 `build-native.ps1` 列为 `None`），纯 IDE 可见性，不影响构建。

---

## 5. 验证证据

环境：macOS 26.6.2（Apple M5）/ Apple clang 21.0.0。**验证方式是把提案源码直接交给 clang 编译、链接、运行**，不经过 CMake（本机无 cmake）。

### 5.1 四目标三元组编译（无任何 ISA 标志）

命令要点（`shim/` 是因为 macOS 不提供 Linux 的 libstdc++ 头，用最小替身补上 `<cstdint>` 与 `<algorithm>`；真实 Linux 上这两个头由标准库提供）：

```bash
clang++ -target <triple> -ffreestanding -std=c++17 -O2 \
        -Wall -Wextra -Werror -fPIC -fvisibility=hidden -c daflegacy_random.cpp
```

| 目标三元组 | 结果 | 导出符号 | `rdseed` 指令 |
| --- | :-: | :-: | :-: |
| `x86_64-unknown-linux-gnu` | ✅ | 2 | 1（真实现） |
| `aarch64-unknown-linux-gnu` | ✅ | 2 | 0（存根） |
| `x86_64-apple-darwin` | ✅ | 2 | 1（真实现） |
| `arm64-apple-darwin` | ✅ | 2 | 0（存根） |

四个目标都是 `-Werror` 零告警，且**没有传 `-mrdseed`**——§3.1 的函数级属性方案有效。

### 5.2 真实现的指令级确认

`x86_64-apple-darwin` 目标文件中 `try_read_sample` 的反汇编：

```text
0000000000000090 <__ZN12_GLOBAL__N_115try_read_sampleEPy>:
      90: 55                 pushq   %rbp
      91: 48 89 e5           movq    %rsp, %rbp
      94: 48 0f c7 f8        rdseedq %rax      ← 真实发出 RDSEED
      98: b9 01 00 00 00     movl    $0x1, %ecx
      9d: 0f 43 c8           cmovael %eax, %ecx
      a0: 73 03              jae     0xa5
      a2: 48 89 07           movq    %rax, (%rdi)
```

RDSEED 只出现在这一个函数里，两个导出入口没有被抬到含 RDSEED 的基线 ISA——这正是 §3.1 选函数级属性而非 TU 级 `-mrdseed` 的效果。

### 5.3 导出符号面

动态库在 `-fvisibility=hidden` 下 `nm -gU` 的结果恰好两个符号，与 `.def` 在 Windows 上声明的集合一致：

```text
0000000000000320 T _daflegacy_rdseed64
0000000000000318 T _daflegacy_rdseed_supported
```

### 5.4 端到端链接与运行

| 架构 | 库 | 测试输出 | 退出码 |
| --- | --- | --- | :-: |
| ARM64（本机） | 链接成功，导出 2 符号 | `RDSEED unavailable, stub contract verified, 0 failure(s).` | 0 |
| x86-64（交叉编译，经 Rosetta 运行） | 链接成功 | `RDSEED unavailable, stub contract verified, 0 failure(s).` | 0 |

x86-64 那行输出是**正确行为而非失败**：本机 CPU 是 Apple M5，没有 RDSEED 位，能力探测如实返回 0，测试于是走了存根契约分支。这同时证明"同一二进制在有/无 RDSEED 的 CPU 上都正确降级"这一设计成立。

### 5.5 未验证项

**必须是"验证过"而不是"看起来对"，以下四项本提案没有覆盖，落地前需补：**

| 项 | 为什么没验 | 怎么补 |
| --- | --- | --- |
| **真实采样路径未执行** | 本机无带 RDSEED 的 x86-64 CPU，`try_read_sample` 只在指令级确认（§5.2），从未真正取到样本 | 在 x86-64 Linux 上跑 `ctest`，期望输出 `RDSEED available, 0 failure(s).` |
| **CMake 未实测** | 本机没有 cmake；`CMakeLists.txt` 与预设从未经过 configure/build | Linux 上 `cmake --preset linux-x64 && cmake --build --preset linux-x64-release && ctest --preset linux-x64-release` |
| **GCC 编译分支未实测** | 本机没有 GCC（`/usr/bin/g++` 是 clang 的别名）；`__builtin_ia32_rdseed_di_step` 只是照 GCC 头文件源码写的 | Linux 上用 GCC 构建一次，重点确认该内建名与 `#pragma` 免标志行为 |
| **MSVC 分支未实测** | 本机没有 MSVC | Windows 上 `build-native.ps1 -Configuration Release -RunTests`，并与改动前的产物行为逐项对齐 |

§5.1–5.4 覆盖的是"代码可移植性"这一个命题；上面四项覆盖的是"三个工具链各自可用"。两者的证据不能互相替代。

### 5.6 附录：`.vcxproj` 为什么不能进跨平台构建（与本提案无关，但常被一并提出）

实测（临时解决方案探针，跑完即删）：

```text
# 仅含 Protocol/Server/SmokeTests 的解决方案      → 构建成功
# 再放入 DAFLegacy.RandomNative.vcxproj            → error MSB4278
#   导入的文件 "$(VCTargetsPath)/Microsoft.Cpp.Default.props" 不存在
```

`MSB4278` 的来源是 `.vcxproj` 第 20 行导入的 Visual Studio C++ 组件，`dotnet` CLI 上不存在。**它由"是否出现在项目图里"决定，与代码是否可移植、与所在目录层级都无关。** 所以本提案即使全部落地，`DAFLegacy.RandomNative` 也仍然不进 `.slnx`，其构建入口永远是独立的 CMake 步骤。附录已记在 [01 · 3.2/3.3](01-target-architecture.md#32-禁止清单)，此处只作交叉印证。

---

## 6. 与 01 / 03 两处文档的差异

| 文档位置 | 现写法 | 应改为 | 依据 |
| --- | --- | --- | --- |
| [01 · 4.4](01-target-architecture.md#44-daflegacyrandomnative服务端可选原生资产可选跨平台化) 阻塞点表、[03 · 4.4](03-build-release-ci.md#44-daflegacyrandomnative-的跨平台化m6可选) | 「Clang/GCC 走 `__cpuid_count` / `__builtin_ia32_rdseed_di_step` / `__builtin_ia32_pause`」 | clang 用 `_rdseed64_step`（无该内建）；GCC 用 `__builtin_ia32_rdseed_di_step`；`_mm_pause()` 三方统一 | §3.2 实测 clang 报 undeclared identifier |
| 同上 | 未提及 `_rdseed64_step` 的目标特性要求 | 补一条：clang 必须标注 `__attribute__((target("rdseed")))`，GCC 由其头文件自理，MSVC 无需 | §3.1 实测 |
| [03 · 4.4](03-build-release-ci.md#44-daflegacyrandomnative-的跨平台化m6可选) | 「CMake 平台判定改写：x64 判定从 `CMAKE_SIZEOF_VOID_P EQUAL 8` 改为检查 `CMAKE_SYSTEM_PROCESSOR`」 | 一致，本提案照此实现（并接受 VS 生成器上报的 `AMD64`） | 已核对 |
| [03 · 4.4](03-build-release-ci.md#44-daflegacyrandomnative-的跨平台化m6可选) | 「新增跨平台 CMake 预设，现有 `build-native.ps1` 内部改为调用预设」 | 本提案只新增非 Windows 预设，`build-native.ps1` 不动 | 该脚本按已装 VS 版本自动挑生成器并有缓存校验逻辑，固定生成器的预设更脆；改动它会给 Windows 引入回归风险 |
| 未记录 | — | CTest 用例在 Release 下因 `NDEBUG` 空跑 | §3.3 |

---

## 7. 落地步骤（若批准）

1. 用 §4 的四份文件替换 `src/DAFLegacy.RandomNative/` 下的 `include/daflegacy_random.h`、`src/daflegacy_random.cpp`、`CMakeLists.txt`、`tests/random_native_tests.cpp`，并新增 `CMakePresets.json`。`DAFLegacy.RandomNative.vcxproj`、`build-native.ps1`、`src/daflegacy_random.def` 不动。
2. Linux：`cmake --preset linux-x64 && cmake --build --preset linux-x64-release && ctest --preset linux-x64-release`，期望 `RDSEED available, 0 failure(s).`，产物 `libDAFLegacy.RandomNative.so`。
3. Windows：`build-native.ps1 -Configuration Release -RunTests`，期望 CTest `rdseed_native` 通过，且 `nativeBridge=True` 行为与改动前一致。
4. 回到 [01 · 4.4](01-target-architecture.md#44-daflegacyrandomnative服务端可选原生资产可选跨平台化) 的阻塞点表逐条勾对，并按 §6 修正文档差异。
5. **不要**顺手把托管侧改掉：Linux 上要真正用上它，还需要 [M2](04-migration-plan.md) 的 RID 探测与 `SetDllImportResolver`，以及解除 `GameRandomSource.cs:198` 的 `OperatingSystem.IsWindows()` 短路。那是另一个提交。
