using Checkers.Core;
using Checkers.Api;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// One JSON object per log line; "move.suggest" lines carry requestId, timeMs, depth, nodes, tablebaseHit.
builder.Logging.ClearProviders().AddJsonConsole(o => o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffK ");
builder.Services.AddProblemDetails();
builder.Services.Configure<EngineOptions>(config.GetSection("Engine"));
builder.Services.AddSingleton<EnginePool>().AddHostedService(sp => sp.GetRequiredService<EnginePool>());
builder.Services.AddSingleton(new LruCache<string, SuggestMoveResponse>(
    config.GetValue("Cache:Capacity", 20000), TimeSpan.FromMinutes(config.GetValue("Cache:TtlMinutes", 15))));
builder.Services.AddTransient<EngineAdapter>().AddTransient<MoveService>();

var app = builder.Build();

// Invalid PDN -> 422, timeouts -> 504, no worker -> 503, as problem details.
app.UseExceptionHandler(errors => errors.Run(async context =>
{
    var e = context.Features.Get<IExceptionHandlerFeature>()!.Error;
    var status = e switch { PdnException => 422, ApiException a => a.Status, BadHttpRequestException b => b.StatusCode, _ => 500 };
    if (status >= 500) app.Logger.LogError(e, "Request {RequestId} failed", context.TraceIdentifier);
    await Results.Problem(e is PdnException or ApiException or BadHttpRequestException ? e.Message : "Unexpected error.",
        statusCode: status, title: e is PdnException ? "Invalid PDN" : null).ExecuteAsync(context);
}));

app.UseDefaultFiles(); // wwwroot/index.html: test board
app.UseStaticFiles();

app.MapPost("/v1/move/suggest", (SuggestMoveRequest request, MoveService moves, HttpContext context) =>
    moves.SuggestAsync(request, context.TraceIdentifier, context.RequestAborted));

app.MapPost("/v1/move/validate", (MoveRequest request) => new { legal = Position.Parse(request.Position).Find(request.Move) != null });

// Helper for the test board (not in the specification): legal moves with the resulting positions.
app.MapPost("/v1/move/legal", (MoveRequest request) =>
{
    var p = Position.Parse(request.Position);
    return new
    {
        position = p.ToFen(),
        sideToMove = p.SideToMove == Side.Black ? "B" : "W",
        moves = p.Moves().Select(m => new
        {
            notation = m.ToString(), from = m.From, path = m.Path, captured = Position.Squares(m.Captured), after = p.Apply(m).ToFen(),
        }),
    };
});

app.MapGet("/healthz", (EnginePool pool) =>
    Results.Json(new { ok = pool.ReadyWorkers > 0, workers = pool.ReadyWorkers }, statusCode: pool.ReadyWorkers > 0 ? 200 : 503));

app.Run();
