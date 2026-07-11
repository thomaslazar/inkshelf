var builder = WebApplication.CreateBuilder(args);

var absUrl = builder.Configuration["ABS_URL"];
if (string.IsNullOrWhiteSpace(absUrl))
    throw new InvalidOperationException("ABS_URL is required.");

builder.Services.AddRazorPages();

var app = builder.Build();
app.UseStaticFiles();
app.MapRazorPages();
app.Run();

public partial class Program { }
