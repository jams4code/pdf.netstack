// Behaves as the test asks, through a file named fake.txt in the folder given to --tessdata-dir, so
// tests running at once do not share anything. Each line of that file is key=value:
//   tsv=path       copy this file to standard output
//   exit=3         the exit code
//   stderr=text    write this to standard error
//   sleep=60000    wait this many milliseconds before anything else
//   flood=1        write to standard output for ever
//   no-stdin=1     exit without reading standard input
// Each run records what it got in that folder: its arguments, one per line (args-PID.txt), the
// variables the engine sets (env-PID.txt), what came in on standard input (stdin-PID.bin), its
// process id before it sleeps (pid-PID.txt) and when it ran (times-PID.txt).
// A file named fake-global.txt beside the executable, in a copy a test made for itself, covers what
// runs without a data folder:
//   version=text     the line --version answers
//   old-streams=1    answer --version and --list-langs on standard error, in the older format
using System.Diagnostics;
using System.Text;

string[] arguments = args;
var global = new Dictionary<string, string>();
string globalFile = Path.Combine(AppContext.BaseDirectory, "fake-global.txt");
if (File.Exists(globalFile))
{
    foreach (string line in File.ReadAllLines(globalFile))
    {
        int equals = line.IndexOf('=');
        if (equals > 0) global[line[..equals]] = line[(equals + 1)..];
    }
}

bool oldStreams = global.ContainsKey("old-streams");
TextWriter answers = oldStreams ? Console.Error : Console.Out;
if (arguments.Contains("--version"))
{
    answers.Write((global.TryGetValue("version", out string? version) ? version : "tesseract 5.9.1") + "\n leptonica-1.85.0\n");
    return 0;
}

int folderAt = Array.IndexOf(arguments, "--tessdata-dir");
if (folderAt < 0 || folderAt + 1 >= arguments.Length)
{
    Console.Error.Write("fake-tesseract needs --tessdata-dir");
    return 64;
}

string folder = arguments[folderAt + 1];
if (arguments.Contains("--list-langs"))
{
    string[] languages = [.. Directory.GetFiles(folder, "*.traineddata").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal)];
    string title = oldStreams ? $"List of available languages ({languages.Length}):" : $"List of available languages in \"{folder}/\" ({languages.Length}):";
    answers.Write($"{title}\n{string.Join("\n", languages)}\n");
    return 0;
}

var settings = new Dictionary<string, string>();
string settingsFile = Path.Combine(folder, "fake.txt");
if (File.Exists(settingsFile))
{
    foreach (string line in File.ReadAllLines(settingsFile))
    {
        int equals = line.IndexOf('=');
        if (equals > 0) settings[line[..equals]] = line[(equals + 1)..];
    }
}

int pid = Environment.ProcessId;
File.WriteAllText(Path.Combine(folder, $"pid-{pid}.txt"), pid.ToString());
File.WriteAllLines(Path.Combine(folder, $"args-{pid}.txt"), arguments);
File.WriteAllLines(Path.Combine(folder, $"env-{pid}.txt"), ["OMP_THREAD_LIMIT=" + Environment.GetEnvironmentVariable("OMP_THREAD_LIMIT"), "LC_ALL=" + Environment.GetEnvironmentVariable("LC_ALL"), "CWD=" + Environment.CurrentDirectory]);
long begin = Stopwatch.GetTimestamp();

if (settings.TryGetValue("sleep", out string? sleep)) Thread.Sleep(int.Parse(sleep));

if (!settings.ContainsKey("no-stdin"))
{
    using FileStream received = File.Create(Path.Combine(folder, $"stdin-{pid}.bin"));
    using Stream input = Console.OpenStandardInput();
    input.CopyTo(received);
}

if (settings.TryGetValue("stderr", out string? error)) Console.Error.Write(error);

using Stream output = Console.OpenStandardOutput();
if (settings.ContainsKey("flood"))
{
    byte[] block = Encoding.ASCII.GetBytes(new string('x', 65536));
    while (true) output.Write(block, 0, block.Length);
}

if (settings.TryGetValue("tsv", out string? tsv))
{
    using FileStream source = File.OpenRead(tsv);
    source.CopyTo(output);
}

output.Flush();
File.WriteAllLines(Path.Combine(folder, $"times-{pid}.txt"), [begin.ToString(), Stopwatch.GetTimestamp().ToString()]);
return settings.TryGetValue("exit", out string? exit) ? int.Parse(exit) : 0;
