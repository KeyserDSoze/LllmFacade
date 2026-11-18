using LllmFacade.Api.Hosting;

// Lets the container image install the Chromium build matching Microsoft.Playwright without PowerShell.
if (args is ["playwright", .. var playwrightArgs])
    return Microsoft.Playwright.Program.Main(playwrightArgs);

var builder = WebApplication.CreateBuilder(args);
builder.AddLllmFacade();
var app = builder.Build();
await app.InitializeDatabaseAsync();
app.UseLllmFacade();
await app.RunAsync();
return 0;

public partial class Program;

