# SSH 主机密钥信任场景测试

在 Windows + .NET 8 SDK 环境运行：

```powershell
dotnet run --project tests\SshHostKeyTrustChecks\SshHostKeyTrustChecks.csproj -c Debug
```

该轻量测试宿主不依赖测试框架/NuGet 包，覆盖首次确认、已知密钥匹配、密钥变化、取消、损坏信任库及管理更新/删除。真实交换机握手仍需人工验收。
