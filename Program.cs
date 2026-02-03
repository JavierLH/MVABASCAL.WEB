using Blazored.LocalStorage;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using SistemaAduanero.Web.Auth;
using SistemaAduanero.Web.Components;
using CurrieTechnologies.Razor.SweetAlert2;


var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
var apiUrl = "http://localhost:5207";
//8081
//5207";

builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(apiUrl)
});
// 1. Servicio de LocalStorage
builder.Services.AddBlazoredLocalStorage();

// 2. Servicio de Autenticación
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<AuthenticationStateProvider, CustomAuthStateProvider>();

// Configuración de Autenticación para soportar [Authorize]
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login"; // Ruta a donde te manda si no tienes permiso
        options.ExpireTimeSpan = TimeSpan.FromMinutes(60);
    }); builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSweetAlert2();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
