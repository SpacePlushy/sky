using System.Text;
using Sky.CelesTrak;
using Sky.Cli;

Console.OutputEncoding = Encoding.UTF8;
return await SkyCli.RunAsync(
    args,
    new CliEnvironment(TimeProvider.System, Console.Out, Console.Error, AppContext.BaseDirectory, "SKY_", CelesTrakClient.CreateHttpClient),
    CancellationToken.None).ConfigureAwait(false);
