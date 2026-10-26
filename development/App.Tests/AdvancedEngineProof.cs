using System.Diagnostics;
using System.Text.Json;
using HunterPdf.Core;

static class AdvancedEngineProof
{
    internal static async Task RunAsync(string root, string output)
    {
        var python = Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe");
        var work = Path.Combine(output, "advanced-transport");
        Directory.CreateDirectory(work);
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks.Add(message);
        }
        AdvancedEngineClient Client(string name, string script, TimeSpan? timeout = null)
        {
            var path = Path.Combine(work, name + " — pipe proof.py");
            File.WriteAllText(path, script);
            return new AdvancedEngineClient(python, path, timeout);
        }
        bool Stopped(AdvancedEngineClient client)
        {
            if (client.LastProcessId is not { } id) return true;
            try { using var process = Process.GetProcessById(id); return process.HasExited; }
            catch (ArgumentException) { return true; }
        }
        async Task Reject<T>(AdvancedEngineClient client, string message, CancellationToken token = default) where T : Exception
        {
            try { await client.RunAsync(new { Probe = "pipe" }, token); }
            catch (T) { Check(Stopped(client), message + "; child process is stopped"); return; }
            throw new Exception(message);
        }
        var success = Client("success", "import sys,json\nr=json.loads(sys.stdin.readline())\nprint(json.dumps({'Success':True,'Data':{'StdinReceived':r['Probe']=='pipe'}}))");
        var result = await success.RunAsync(new { Probe = "pipe", Password = "synthetic-not-a-real-password" });
        Check(result.GetProperty("StdinReceived").GetBoolean() && Stopped(success), "Stdin JSON reaches an isolated interpreter through a spaced Unicode script path");
        await Reject<InvalidOperationException>(Client("refused", "import sys,json\nsys.stdin.readline()\nprint(json.dumps({'Success':False,'Error':'Unsupported test input'}))"), "Explicit engine refusal is surfaced");
        await Reject<IOException>(Client("invalid-json", "import sys\nsys.stdin.readline()\nprint('invalid-json')"), "Malformed output is rejected");
        await Reject<IOException>(Client("missing-data", "import sys\nsys.stdin.readline()\nprint('{\"Success\":true}')"), "A success without a result is rejected");
        await Reject<IOException>(Client("nonzero", "import sys\nsys.stdin.readline()\nsys.stderr.write('Synthetic engine failure')\nsys.exit(3)"), "Nonzero engine exit is surfaced");
        await Reject<IOException>(Client("stdout-limit", "import sys,time\nsys.stdin.readline()\nsys.stdout.write('x'*70000)\nsys.stdout.flush()\ntime.sleep(60)"), "Oversized stdout terminates the engine");
        await Reject<IOException>(Client("stderr-limit", "import sys,time\nsys.stdin.readline()\nsys.stderr.write('x'*70000)\nsys.stderr.flush()\ntime.sleep(60)"), "Oversized stderr terminates the engine");
        var stalled = "import sys,time\nsys.stdin.readline()\ntime.sleep(60)";
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(700)))
            await Reject<OperationCanceledException>(Client("cancel", stalled), "Active cancellation terminates the engine", cancellation.Token);
        await Reject<TimeoutException>(Client("timeout", stalled, TimeSpan.FromMilliseconds(700)), "A deadline is distinct from user cancellation");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var notStarted = Client("pre-cancel", stalled);
            await Reject<OperationCanceledException>(notStarted, "Pre-cancelled work never starts an engine", cancellation.Token);
            Check(notStarted.LastProcessId is null, "No process is created for pre-cancelled work");
        }
        File.WriteAllText(Path.Combine(work, "results.json"), JsonSerializer.Serialize(new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} advanced transport checks. Evidence: {work}");
    }
}
