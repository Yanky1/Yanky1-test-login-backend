using Microsoft.Extensions.Options;
using Solvelt.Api.Options;

namespace Solvelt.Api.Services;

public interface IFileStorage
{
    Task SaveAsync(string key, Stream content, CancellationToken ct = default);
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

public class LocalFileStorage : IFileStorage
{
    private readonly string _root;
    public LocalFileStorage(IOptions<StorageOptions> opt, IHostEnvironment env)
    {
        _root = Path.GetFullPath(Path.IsPathRooted(opt.Value.LocalRoot) ? opt.Value.LocalRoot : Path.Combine(env.ContentRootPath, opt.Value.LocalRoot));
        Directory.CreateDirectory(_root);
    }

    private string PathFor(string key)
    {
        if (!Guid.TryParseExact(key, "N", out _)) throw new ArgumentException("Invalid storage key");
        return Path.Combine(_root, key);
    }

    public async Task SaveAsync(string key, Stream content, CancellationToken ct = default)
    {
        await using var fs = File.Create(PathFor(key));
        await content.CopyToAsync(fs, ct);
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var p = PathFor(key);
        return Task.FromResult<Stream?>(File.Exists(p) ? File.OpenRead(p) : null);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var p = PathFor(key);
        if (File.Exists(p)) File.Delete(p);
        return Task.CompletedTask;
    }
}
