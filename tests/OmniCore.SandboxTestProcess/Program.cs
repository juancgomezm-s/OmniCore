using System.Diagnostics;
using System.Net.Sockets;

if (args.Length == 2 && args[0] == "read")
{
    try
    {
        Console.Write(File.ReadAllText(args[1]));
        return 0;
    }
    catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
    {
        Console.Write("DENIED");
        return 23;
    }
}

if (args.Length == 2 && args[0] == "spawn")
{
    var child = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        ArgumentList = { "child", args[1] },
        UseShellExecute = false,
        CreateNoWindow = true
    }) ?? throw new InvalidOperationException("Failed to start child process.");
    Console.WriteLine(child.Id);
    await child.WaitForExitAsync();
    return 0;
}

if (args.Length == 2 && args[0] == "child")
{
    var grandchild = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        ArgumentList = { "sleep" },
        UseShellExecute = false,
        CreateNoWindow = true
    }) ?? throw new InvalidOperationException("Failed to start grandchild process.");
    File.WriteAllText(args[1], grandchild.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Console.WriteLine(grandchild.Id);
    await grandchild.WaitForExitAsync();
    return 0;
}

if (args.Length == 2 && args[0] == "connect")
{
    try
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", int.Parse(args[1]), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Console.Write("CONNECTED");
        return 0;
    }
    catch (Exception exception) when (exception is SocketException or TimeoutException or OperationCanceledException)
    {
        Console.Write("DENIED");
        return 24;
    }
}

if (args.Length == 1 && args[0] == "sleep")
{
    await Task.Delay(TimeSpan.FromMinutes(10));
    return 0;
}

return 2;
