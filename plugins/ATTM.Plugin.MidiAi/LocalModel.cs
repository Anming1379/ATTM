using System.IO;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;

namespace ATTM.Plugin.MidiAi;

/// <summary>
/// 进程内推理：直接加载 GGUF 模型跑 llama.cpp，不再需要外部服务。
/// 同一份模型只加载一次，之后复用（首次加载几秒钟）。
/// </summary>
internal sealed class LocalModel : IDisposable
{
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private string? _loadedPath;
    private static string? _nativeSearchPath;

    public string? LoadedPath => _loadedPath;

    /// <summary>把插件目录加进原生库搜索路径（llama.dll 跟着插件走）。</summary>
    public static void PrepareNative(string pluginDirectory)
    {
        if (_nativeSearchPath == pluginDirectory) return;

        try
        {
            var nativeRoot = Path.Combine(pluginDirectory, "runtimes", "win-x64", "native");

            var directories = new List<string> { pluginDirectory };
            if (Directory.Exists(nativeRoot))
            {
                directories.Add(nativeRoot);
                directories.AddRange(Directory.GetDirectories(nativeRoot));
            }

            NativeLibraryConfig.All.WithSearchDirectories(directories);
            _nativeSearchPath = pluginDirectory;
        }
        catch
        {
            // 配置失败就让 llama.cpp 用默认查找逻辑
        }
    }

    public void EnsureLoaded(string modelPath, int contextSize)
    {
        if (string.Equals(_loadedPath, modelPath, StringComparison.OrdinalIgnoreCase) && _context is not null) return;

        if (!File.Exists(modelPath)) throw new FileNotFoundException("模型文件不存在：" + modelPath);

        Dispose();

        var parameters = new ModelParams(modelPath)
        {
            ContextSize = (uint)Math.Clamp(contextSize, 512, 8192),
            GpuLayerCount = 0
        };

        _weights = LLamaWeights.LoadFromFile(parameters);
        _context = _weights.CreateContext(parameters);
        _loadedPath = modelPath;
    }

    public string Complete(string system, string user, int maxTokens, CancellationToken token)
    {
        if (_context is null) throw new InvalidOperationException("模型还没加载");

        var executor = new InteractiveExecutor(_context);
        var session = new ChatSession(executor);
        var history = new ChatHistory();
        history.AddMessage(AuthorRole.System, system);
        history.AddMessage(AuthorRole.User, user);

        var inference = new InferenceParams
        {
            MaxTokens = Math.Clamp(maxTokens, 128, 4096),
            AntiPrompts = new List<string> { "User:", "<|im_end|>" }
        };

        var builder = new StringBuilder();

        var task = Task.Run(async () =>
        {
            await foreach (var piece in session.ChatAsync(history, inference, token).ConfigureAwait(false))
                builder.Append(piece);
        }, token);

        task.GetAwaiter().GetResult();
        return builder.ToString();
    }

    public void Dispose()
    {
        _context?.Dispose();
        _weights?.Dispose();
        _context = null;
        _weights = null;
        _loadedPath = null;
    }
}
