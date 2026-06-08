namespace OpenCodex.CoreBase.Domain;

/// <summary>
/// 表示创建管理员托管访问密钥所需的数据。
/// </summary>
public sealed class ApiKeyCreateCommand
{
    /// <summary>
    /// 初始化 <see cref="ApiKeyCreateCommand"/> 类的新实例。
    /// </summary>
    /// <param name="ownerUsername">将拥有该访问密钥的用户名。</param>
    /// <param name="name">访问密钥的显示名称。</param>
    public ApiKeyCreateCommand(string ownerUsername, string name)
    {
        OwnerUsername = ownerUsername;
        Name = name;
    }

    /// <summary>
    /// 获取将拥有该访问密钥的用户名。
    /// </summary>
    public string OwnerUsername { get; }

    /// <summary>
    /// 获取访问密钥的显示名称。
    /// </summary>
    public string Name { get; }
}

/// <summary>
/// 表示更新管理员托管访问密钥所需的数据。
/// </summary>
public sealed class ApiKeyUpdateCommand
{
    /// <summary>
    /// 初始化 <see cref="ApiKeyUpdateCommand"/> 类的新实例。
    /// </summary>
    /// <param name="enabled">指示该访问密钥是否应启用的值。</param>
    public ApiKeyUpdateCommand(bool enabled)
    {
        Enabled = enabled;
    }

    /// <summary>
    /// 获取指示该访问密钥是否应启用的值。
    /// </summary>
    public bool Enabled { get; }
}
