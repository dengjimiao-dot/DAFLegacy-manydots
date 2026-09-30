#!/bin/bash
# 每 300ms 采样一次 loopback 频道相关连接状态，持续 300 秒
out="netstat_samples.txt"
: > "$out"
end=$((SECONDS+300))
while [ $SECONDS -lt $end ]; do
  ts=$(date +%H:%M:%S.%3N)
  netstat -ano -p tcp | grep "127.0.0.1" | grep -E ":(200[0-9][0-9]|7001|2311|7002|7003) *127" | while read -r line; do
    echo "$ts $line"
  done >> "$out"
  sleep 0.3
done
echo "SAMPLER DONE" >> "$out"
