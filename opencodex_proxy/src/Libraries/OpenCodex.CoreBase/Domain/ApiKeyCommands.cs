namespace OpenCodex.CoreBase.Domain;

/// <summary>
/// 表示创建管理员托管访问密钥所需的数据。
/// </summary>
public sealed class ApiKeyCreateCommand
{
    /// <summary>
    /// 初始化 <see cref="ApiKeyCreateCommand"/> 类的新实例。
    /// </summary>
    /// <param name="ownerUserId">将拥有该访问密钥的用户标识符。</param>
    /// <param name="name">访问密钥的显示名称。</param>
    public ApiKeyCreateCommand(Guid ownerUserId, string name)
    {
        OwnerUserId = ownerUserId;
        Name = name;
    }

    /// <summary>
    /// 获取将拥有该访问密钥的用户标识符。
    /// </summary>
    public Guid OwnerUserId { get; }

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

/// <summary>
/// 表示导入管理员托管访问密钥所需的数据。
/// </summary>
public sealed class ApiKeyImportCommand
{
    /// <summary>
    /// 初始化 <see cref="ApiKeyImportCommand"/> 类的新实例。
    /// </summary>
    /// <param name="items">导入的访问密钥条目列表。</param>
    public ApiKeyImportCommand(IReadOnlyList<ApiKeyImportItem> items)
    {
        Items = items;
    }

    /// <summary>
    /// 获取导入的访问密钥条目列表。
    /// </summary>
    public IReadOnlyList<ApiKeyImportItem> Items { get; }
}

/// <summary>
/// 表示导入的单个访问密钥条目。
/// </summary>
public sealed class ApiKeyImportItem
{
    /// <summary>
    /// 初始化 <see cref="ApiKeyImportItem"/> 类的新实例。
    /// </summary>
    /// <param name="ownerUsername">拥有者用户名。</param>
    /// <param name="name">访问密钥显示名称。</param>
    /// <param name="key">访问密钥明文。</param>
    /// <param name="enabled">指示访问密钥是否启用。</param>
    public ApiKeyImportItem(string? ownerUsername, string name, string key, bool enabled)
    {
        OwnerUsername = ownerUsername;
        Name = name;
        Key = key;
        Enabled = enabled;
    }

    /// <summary>
    /// 获取拥有者用户名。
    /// </summary>
    public string? OwnerUsername { get; }

    /// <summary>
    /// 获取访问密钥显示名称。
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// 获取访问密钥明文。
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// 获取指示访问密钥是否启用的值。
    /// </summary>
    public bool Enabled { get; }
}
