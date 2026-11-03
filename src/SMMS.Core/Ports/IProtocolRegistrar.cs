namespace SMMS.Core.Ports;

/// <summary>
/// 注册自定义 URL 协议处理器（SMMS 需要 <c>nxm://</c>）。
/// 返回 <c>false</c> 表示当前平台或权限下不支持，调用方据此降级，而不是抛异常中断流程。
/// </summary>
public interface IProtocolRegistrar
{
    bool IsRegistered(string scheme);

    bool Register(string scheme, string executablePath);

    bool Unregister(string scheme);
}
