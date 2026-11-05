using DevTools.Core.Model;

namespace DevTools.Scan;

public sealed record FrameworkInfo(string Id, string Name, Ecosystem Ecosystem);

/// <summary>
/// The frameworks the scanner recognises. Deliberately a short, curated list of application
/// frameworks: libraries in general are out of scope, and so is anything that is a toolchain
/// (runtimes, compilers, package managers and build tools live in <see cref="ToolKind"/>).
/// </summary>
public static class FrameworkCatalog
{
    public static IReadOnlyList<FrameworkInfo> All { get; } =
    [
        new("react", "React", Ecosystem.Node),
        new("next", "Next.js", Ecosystem.Node),
        new("vue", "Vue", Ecosystem.Node),
        new("nuxt", "Nuxt", Ecosystem.Node),
        new("angular", "Angular", Ecosystem.Node),
        new("svelte", "Svelte", Ecosystem.Node),
        new("sveltekit", "SvelteKit", Ecosystem.Node),
        new("solid", "SolidJS", Ecosystem.Node),
        new("astro", "Astro", Ecosystem.Node),
        new("remix", "Remix", Ecosystem.Node),
        new("express", "Express", Ecosystem.Node),
        new("fastify", "Fastify", Ecosystem.Node),
        new("nestjs", "NestJS", Ecosystem.Node),
        new("electron", "Electron", Ecosystem.Node),
        new("react-native", "React Native", Ecosystem.Node),
        new("expo", "Expo", Ecosystem.Node),
        new("spring-boot", "Spring Boot", Ecosystem.Java),
        new("spring-framework", "Spring Framework", Ecosystem.Java),
        new("quarkus", "Quarkus", Ecosystem.Java),
        new("micronaut", "Micronaut", Ecosystem.Java),
        new("javalin", "Javalin", Ecosystem.Java),
        new("vaadin", "Vaadin", Ecosystem.Java),
        new("dropwizard", "Dropwizard", Ecosystem.Java),
        new("helidon", "Helidon", Ecosystem.Java),
        new("aspnet-core", "ASP.NET Core", Ecosystem.DotNet),
        new("blazor", "Blazor", Ecosystem.DotNet),
        new("maui", ".NET MAUI", Ecosystem.DotNet),
        new("avalonia", "Avalonia", Ecosystem.DotNet),
        new("django", "Django", Ecosystem.Python),
        new("flask", "Flask", Ecosystem.Python),
        new("fastapi", "FastAPI", Ecosystem.Python),
        new("rails", "Ruby on Rails", Ecosystem.Ruby),
        new("sinatra", "Sinatra", Ecosystem.Ruby),
        new("laravel", "Laravel", Ecosystem.Php),
        new("symfony", "Symfony", Ecosystem.Php),
        new("slim", "Slim", Ecosystem.Php),
        new("flutter", "Flutter", Ecosystem.Dart),
        new("gin", "Gin", Ecosystem.Go),
        new("echo", "Echo", Ecosystem.Go),
        new("fiber", "Fiber", Ecosystem.Go),
        new("axum", "Axum", Ecosystem.Rust),
        new("actix-web", "Actix Web", Ecosystem.Rust),
        new("rocket", "Rocket", Ecosystem.Rust),
        new("tauri", "Tauri", Ecosystem.Rust),
        new("phoenix", "Phoenix", Ecosystem.Elixir)
    ];

    private static readonly Dictionary<string, FrameworkInfo> ById = All.ToDictionary(f => f.Id, StringComparer.Ordinal);

    public static FrameworkInfo Get(string id) => ById[id];

    public static bool TryGet(string id, out FrameworkInfo info) => ById.TryGetValue(id, out info!);
}
