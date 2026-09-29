namespace DFLegacy.Server;

// 服务端裁决随机的唯一取值缝。42 处业务调用点只依赖此接口，
// 生产实现是 GameRandomSource.Shared，确定性替身在自测项目中。
public interface IDropRandomSource
{
    int Next(int exclusiveMaximum);
}
