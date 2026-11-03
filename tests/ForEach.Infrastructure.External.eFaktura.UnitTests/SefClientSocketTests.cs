using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ForEach.Infrastructure.External.eFaktura.UnitTests;

/// <summary>
/// The client against a real socket, because a fake handler cannot see the one mistake that matters
/// here: a request disposed before it is sent.
///
/// <c>GetAsync</c>, <c>PostJsonAsync</c> and the changes call used to build their request in a
/// <c>using</c> and return the exchange task without awaiting it. A fake handler is handed the
/// request object whatever state it is in, so every test passed while the real transport found a
/// disposed request and a disposed body. Only bytes arriving at a listener prove they were sent.
/// </summary>
public class SefClientSocketTests
{
    private sealed record Arrived(string Method, string Url, string? ApiKey, string Body, long ContentLength);

    [Fact]
    public async Task A_json_post_and_a_changes_call_actually_arrive_with_their_bodies()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var arrived = new List<Arrived>();

        var server = Task.Run(async () =>
        {
            // Three calls are made below; each is answered with an empty JSON document or list.
            for (var served = 0; served < 3; served++)
            {
                var context = await listener.GetContextAsync();

                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);

                arrived.Add(new Arrived(
                    context.Request.HttpMethod,
                    context.Request.RawUrl ?? "",
                    context.Request.Headers["ApiKey"],
                    await reader.ReadToEndAsync(),
                    context.Request.ContentLength64));

                var answer = Encoding.UTF8.GetBytes(context.Request.RawUrl!.Contains("changes", StringComparison.Ordinal) ? "[]" : "{}");

                context.Response.StatusCode = 200;
                await context.Response.OutputStream.WriteAsync(answer);
                context.Response.Close();
            }
        });

        var client = new SefClient(
            new HttpClient(),
            new SefCredentials { BaseUrl = $"http://127.0.0.1:{port}", ApiKey = "tajni-kljuc", TimeoutSeconds = 10 });

        var cancel = await client.CancelSalesInvoiceAsync(4242, "Greška u dokumentu");
        var changes = await client.GetSalesInvoiceChangesAsync(new DateTime(2026, 11, 3));
        var read = await client.GetSalesInvoiceAsync(4242);

        await server.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();

        Assert.True(cancel.IsSuccess, cancel.FailureMessage);
        Assert.True(changes.IsSuccess, changes.FailureMessage);
        Assert.True(read.IsSuccess, read.FailureMessage);

        var post = arrived[0];
        Assert.Equal("POST", post.Method);
        Assert.Equal("tajni-kljuc", post.ApiKey);
        Assert.Contains("\"invoiceId\":4242", post.Body, StringComparison.Ordinal);
        Assert.Contains("Greška u dokumentu", post.Body, StringComparison.Ordinal);

        // The changes call is a POST with an explicitly empty body: without a Content-Length SEF's
        // front end answers 411 before looking at the key.
        var change = arrived[1];
        Assert.Equal("POST", change.Method);
        Assert.Contains("changes?date=2026-11-03", change.Url, StringComparison.Ordinal);
        Assert.Equal(0, change.ContentLength);

        Assert.Equal("GET", arrived[2].Method);
        Assert.Contains("invoiceId=4242", arrived[2].Url, StringComparison.Ordinal);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();

        var port = ((IPEndPoint)probe.LocalEndpoint).Port;

        probe.Stop();

        return port;
    }
}
