using System.Net;
using System.Text;

namespace KioskWatchdog.TestApp;

internal static class Program
{
    private static int _healthPort = 3000;
    private static bool _healthOk = true;
    private static HttpListener? _listener;

    public static async Task<int> Main(string[] args)
    {
        var mode = ParseMode(args);
        Console.WriteLine($"KioskWatchdog.TestApp starting in mode: {mode}");

        switch (mode)
        {
            case "crash":
                Console.WriteLine("Simulating crash (exit code 1).");
                return 1;

            case "exit-after":
                var seconds = ParseExitAfterSeconds(args);
                StartHealthServer(healthy: true);
                Console.WriteLine($"Exiting after {seconds} seconds.");
                await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
                StopHealthServer();
                return 0;

            case "hang":
                // Process stays alive but health endpoint stops responding.
                StartHealthServer(healthy: true);
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                Console.WriteLine("Entering hang mode — health endpoint stopped.");
                StopHealthServer();
                await WaitUntilCancelled().ConfigureAwait(false);
                return 0;

            case "health-fail":
                StartHealthServer(healthy: false);
                Console.WriteLine("Running with failing health endpoint.");
                await WaitUntilCancelled().ConfigureAwait(false);
                StopHealthServer();
                return 0;

            case "normal":
            default:
                StartHealthServer(healthy: true);
                Console.WriteLine($"Healthy. Listening on http://127.0.0.1:{_healthPort}/health");
                await WaitUntilCancelled().ConfigureAwait(false);
                StopHealthServer();
                return 0;
        }
    }

    private static string ParseMode(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--normal" or "--crash" or "--hang" or "--health-fail")
                return arg.TrimStart('-');

            if (arg == "--exit-after")
                return "exit-after";

            if (arg.StartsWith("--port=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(arg["--port=".Length..], out var port))
            {
                _healthPort = port;
            }

            if (arg == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out port))
            {
                _healthPort = port;
            }
        }

        return "normal";
    }

    private static int ParseExitAfterSeconds(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--exit-after" && i + 1 < args.Length
                && int.TryParse(args[i + 1], out var seconds)
                && seconds > 0)
            {
                return seconds;
            }
        }

        return 5;
    }

    private static void StartHealthServer(bool healthy)
    {
        _healthOk = healthy;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_healthPort}/");
        _listener.Start();
        _ = Task.Run(ListenLoop);
    }

    private static async Task ListenLoop()
    {
        if (_listener is null)
            return;

        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleRequest(context));
        }
    }

    private static void HandleRequest(HttpListenerContext context)
    {
        try
        {
            if (context.Request.Url?.AbsolutePath.Equals("/health", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (_healthOk)
                {
                    WriteJson(context.Response, 200, """{"status":"ok"}""");
                }
                else
                {
                    WriteJson(context.Response, 503, """{"status":"fail"}""");
                }
            }
            else
            {
                WriteJson(context.Response, 404, """{"error":"not found"}""");
            }
        }
        catch
        {
            try { context.Response.Abort(); } catch { /* ignore */ }
        }
    }

    private static void WriteJson(HttpListenerResponse response, int statusCode, string body)
    {
        var buffer = Encoding.UTF8.GetBytes(body);
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.OutputStream.Close();
    }

    private static void StopHealthServer()
    {
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch
        {
            // ignore
        }
        finally
        {
            _listener = null;
        }
    }

    private static async Task WaitUntilCancelled()
    {
        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            tcs.TrySetResult();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => tcs.TrySetResult();
        await tcs.Task.ConfigureAwait(false);
    }
}
