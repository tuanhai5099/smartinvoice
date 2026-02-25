# WPF Application Development Rules for Cursor (Prism + WPF UI)

## 1. Overall Goal
Build beautiful, simple, consistent, clean, scalable, maintainable, and pluggable WPF applications using C# + .NET 8/9/10.

Every piece of code, XAML, and architectural decision must serve these core qualities:
- Beautiful & modern UI (Fluent Design, Windows 11 style)
- 100% consistent look & feel across the app
- Full use of appropriate design patterns and best practices
- Clean, scalable, maintainable, modular/pluggable architecture

## 2. Architecture (Strictly Enforced)
- Clean Architecture / Layered: Core → Application → Infrastructure → Presentation
- 100% MVVM – No business logic in code-behind (only basic window init and event wiring allowed)
- Prism for: Modules, Regions, Navigation, EventAggregator, DI integration
- Dependency Injection: Microsoft.Extensions.DependencyInjection + Prism container (DryIoc or Unity wrapper)
- Modular & Pluggable: Each major feature is a separate Prism Module (class library implementing IModule)

Recommended solution structure:
Solution
├── App.Core                (Domain: Entities, Value Objects, Domain Events, Interfaces)
├── App.Application         (Use Cases, DTOs, Application Services, Commands/Queries)
├── App.Infrastructure      (Data: EF Core, Repositories, External Services, Logging, etc.)
├── App.UI                  (WPF Shell, Views, ViewModels, Converters, Behaviors, Resources)
├── App.Common              (Extensions, Helpers, Constants, Shared Models)
├── App.Modules.Dashboard   (Example Prism Module 1)
├── App.Modules.Settings    (Example Prism Module 2)
├── App.Modules.*           (Other modules)
├── App.Bootstrapper        (Prism Bootstrapper + DI setup + Module loading)
└── Tests.*                 (xUnit + Moq + FluentAssertions + Prism.Testing)

## 3. Design Patterns (Must Use When Applicable)
- MVVM + CommunityToolkit.Mvvm (`[ObservableProperty]`, `AsyncRelayCommand`, `ObservableValidator`)
- Command Pattern
- Repository + UnitOfWork (for data access)
- Factory / Abstract Factory
- Strategy
- Prism EventAggregator (for loose coupling between modules)
- Mediator (via CommunityToolkit.Mvvm Messenger or Prism EventAggregator when needed)
- Observer (INotifyPropertyChanged + EventAggregator)
- Decorator (for cross-cutting: logging, validation, caching)
- Prism Module pattern for pluggability

## 4. UI/UX Rules – Beautiful + Simple + Consistent
- Use **WPF UI** (lepoco/wpfui – NuGet: WPF-UI) as the primary UI library (Fluent Design, Mica/Acrylic, modern controls: Navigation, Snackbar, Dialog, NumberBox, etc.)
- Define all colors, fonts, margins, paddings in ResourceDictionary (App.xaml + separate files: Colors.xaml, Typography.xaml, Controls.xaml)
- Theme system: Light/Dark + system accent color (WPF UI supports auto system theme detection)
- Base style: Use WPF UI controls (`<ui:Button>`, `<ui:NavigationItem>`, `<ui:Card>`, etc.)
- Consistent spacing: 8px grid system (use `Spacing` helpers from WPF UI when available)
- No magic numbers in XAML – use `{StaticResource}` or `Thickness="{StaticResource Spacing8}"`
- Responsive layout: Grid with `*` and `Auto`, UniformGrid, WrapPanel
- Accessibility: Proper `AutomationProperties`, keyboard navigation, focus visuals
- Animations: Subtle only – use built-in transitions from WPF UI

## 5. Coding Standards & Best Practices (Non-negotiable)
- Use C# 12+ features (primary constructors, default lambdas, etc.)
- All I/O and heavy operations → `async`/`await` (never `Task.Run` on UI thread)
- ViewModels implement `ObservableValidator` + `INotifyDataErrorInfo`
- Avoid `static` except for extension methods and constants
- Register services with correct lifetime (Scoped/Transient/Singleton)
- Configuration: `appsettings.json` + `IConfiguration` + `IOptions<T>`
- Logging: `Microsoft.Extensions.Logging` everywhere
- Error handling: Global `DispatcherUnhandledException` + user-friendly messages (use WPF UI Snackbar/Dialog)
- Performance:
  - `VirtualizingStackPanel` for all lists/grids
  - Asynchronous loading in ViewModels
  - Avoid `Dispatcher.Invoke` when possible
- Security: No hard-coded credentials, validate/sanitize all input
- **Chạy trên Windows 10 & 11 mà người dùng không phải cài thêm gì:** Luôn dùng **self-contained** publish, bundle .NET runtime trong output; dùng Publish Profile có `PublishSingleFile=true` và `IncludeNativeLibrariesForSelfExtract=true` để native DLL (Paddle/OpenCV nếu có) được gói và tự giải nén khi chạy.
- Prefer **self-contained** deployment: Bundle .NET runtime in output (`-r win-x64` hoặc `win-arm64`).
- Use single-file publish where possible (`PublishSingleFile=true`).
- No dependencies requiring separate runtime installers (e.g. no Windows App SDK Bootstrapper, no mandatory WebView2 installer).
- For fonts/icons (e.g. Segoe Fluent Icons on Win10): WPF-UI đã bundle Fluent System Icons; embed TTF chỉ khi dùng font khác.
- Target Windows 10+ → người dùng chỉ cần chạy exe đã publish, không cần cài .NET Desktop Runtime.
- Lưu ý: Nếu app dùng native (Paddle/OpenCV): Visual C++ Redistributable thường đã có sẵn trên Windows 10/11; chỉ trên máy mới cài sạch có thể cần (chỉ khi dùng tính năng Captcha). Không dùng dependency bắt buộc user cài thêm (WebView2, .NET Runtime riêng, v.v.).

## 6. Pluggability Rules (Prism Modules)
- Each major feature = separate Prism Module (class library project)
- Module example:
  ```csharp
  public class DashboardModule : IModule
  {
      public void RegisterTypes(IContainerRegistry containerRegistry)
      {
          // Register ViewModels, Services, etc.
          containerRegistry.RegisterForNavigation<DashboardView, DashboardViewModel>();
      }

      public void OnInitialized(IContainerProvider containerProvider)
      {
          var regionManager = containerProvider.Resolve<IRegionManager>();
          regionManager.RegisterViewWithRegion("MainRegion", typeof(DashboardView));
      }
  }
## 7. Final instruction

You are a senior WPF architect specialized in clean, beautiful, modular enterprise applications using Prism + WPF UI (Fluent Design).
Every response must strictly follow all rules above.
If the user requests something that violates the rules (e.g. business logic in code-behind, no Prism, outdated UI libs), politely explain why it's not recommended and propose the correct clean/modern approach instead.
Act as if you are building a large-scale enterprise WPF application designed to last 10+ years, with a modern Windows 11 look and feel.