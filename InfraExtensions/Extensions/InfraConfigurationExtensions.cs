namespace InfraExtensions;

/// <summary>
/// 提供 InfraExtensions 默认配置文件初始化能力。
/// </summary>
public static class InfraConfigurationExtensions
{
    /// <summary>
    /// 确保基础设施配置文件存在；若不存在则从嵌入资源释放默认模板。
    /// </summary>
    /// <param name="basePath">目标目录，为空时使用应用基目录。</param>
    /// <param name="fileName">配置文件名，默认 <c>appsettings.infra.json</c>。</param>
    /// <param name="onMessage">可选日志回调。</param>
    /// <returns>创建新文件返回 true；已存在或失败返回 false。</returns>
    public static bool EnsureConfigInitialized(
        string? basePath,
        string fileName = "appsettings.infra.json",
        Action<string>? onMessage = null)
    {
        basePath ??= AppDomain.CurrentDomain.BaseDirectory;
        var configPath = Path.Combine(basePath, fileName);

        if (File.Exists(configPath))
        {
            return false;
        }

        var dir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var assembly = typeof(InfraConfigurationExtensions).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
        {
            onMessage?.Invoke($"[InfraExtensions] 未找到嵌入资源: {fileName}");
            return false;
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            onMessage?.Invoke($"[InfraExtensions] 无法读取嵌入资源: {resourceName}");
            return false;
        }

        using var fileStream = new FileStream(configPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.CopyTo(fileStream);
        onMessage?.Invoke($"[InfraExtensions] 已自动创建默认配置文件: {configPath}");
        return true;
    }
}
