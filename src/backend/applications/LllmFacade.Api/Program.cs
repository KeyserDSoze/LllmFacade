using LllmFacade.Business;
using LllmFacade.Infrastructure.Memory;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<InMemoryProjectStore>();
var app = builder.Build();

// The first API has no authentication: block accidental Internet production use.
if (!app.Environment.IsDevelopment())
    throw new InvalidOperationException("La API demo funziona solo in ambiente Development.");

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/projects", (InMemoryProjectStore store) => Results.Ok(store.List()));
app.MapPost("/api/projects", (CreateProjectRequest request, InMemoryProjectStore store) =>
{
    try
    {
        var name = ProjectRules.NormalizeName(request.Name);
        var project = store.Create(name);
        return Results.Created("/api/projects/" + project.Id, project);
    }
    catch (ArgumentException error)
    {
        return Results.BadRequest(new { error = error.Message });
    }
});
app.Run();

public sealed record CreateProjectRequest(string? Name);

