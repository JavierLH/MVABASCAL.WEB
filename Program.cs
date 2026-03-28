using Blazored.LocalStorage;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using SistemaAduanero.Web.Auth;
using SistemaAduanero.Web.Components;
using SistemaAduanero.Web.Services;
using CurrieTechnologies.Razor.SweetAlert2;


var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
var apiUrl = "http://localhost:5207";
//8081
//5207";

// HTTP Clients
builder.Services.AddTransient<ApiAuthorizationMessageHandler>();

builder.Services.AddScoped(sp => 
{
    var handler = sp.GetRequiredService<ApiAuthorizationMessageHandler>();
    handler.InnerHandler = new HttpClientHandler();
    return new HttpClient(handler) { BaseAddress = new Uri(apiUrl) };
});
// 1. Servicio de LocalStorage
builder.Services.AddBlazoredLocalStorage();

// 2. Servicio de Autenticacin
builder.Services.AddAuthorizationCore(options =>
{
    
    options.AddPolicy("TransmitirVucem", policy =>
        policy.RequireClaim("Permission", "vucem:transmitir"));

    options.AddPolicy("LeerProforma", policy =>
        policy.RequireClaim("Permission", "proforma:leer"));

    options.AddPolicy("EnviarDigitalizacionVucem", policy =>
        policy.RequireClaim("Permission", "vucem:digitalizar"));

    options.AddPolicy("EditarExpediente", policy =>
        policy.RequireClaim("Permission", "manifestacion:editar"));

    options.AddPolicy("CamposAgenteAduanal", policy =>
        policy.RequireClaim("Permission", "campos_aa:editar"));
});


builder.Services.AddScoped<AuthenticationStateProvider, CustomAuthStateProvider>();
builder.Services.AddScoped<SistemaAduanero.Web.Services.TermsStateService>();

// Configuración de Autenticación para soportar [Authorize] en SSR
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(60);
    });

 builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();



builder.Services.AddHttpClient("ApiClient", client =>
{
    client.BaseAddress = new Uri(apiUrl);
}).AddHttpMessageHandler<ApiAuthorizationMessageHandler>();


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
