using AwesomeAssertions;
using Bunit;
using Croft.Web.Components;
using Croft.Web.Components.Layout;
using Croft.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Croft.Web.Tests.Components;

/// <summary>
/// Brief 13i item 4 (M-20): a component that imports a JavaScript module releases the module
/// reference when it goes away, and a circuit that is already gone does not turn that into an error.
/// </summary>
public class JsModuleDisposalTests : BlogBunitContext
{
    private readonly RecordingJsRuntime _js = new();

    public JsModuleDisposalTests()
    {
        Services.AddSingleton<IJSRuntime>(_js);
        Services.AddSingleton(new MudBlazor.MudTheme());
    }

    [Fact]
    public async Task PostBody_DisposesItsPostModule()
    {
        Render<PostBody>(p => p.Add(x => x.Markdown, new RenderedMarkdown("<p>x</p>", [])));
        _js.ModulesFor("./js/post.js").Should().NotBeEmpty().And.OnlyContain(m => !m.Disposed, "the module is in use while the body is shown");

        await DisposeComponentsAsync();

        _js.ModulesFor("./js/post.js").Should().OnlyContain(m => m.Disposed);
    }

    [Fact]
    public async Task ThemeSwitch_DisposesItsShellModule()
    {
        Render<ThemeSwitch>();
        _js.ModulesFor("./js/shell.js").Should().NotBeEmpty().And.OnlyContain(m => !m.Disposed);

        await DisposeComponentsAsync();

        _js.ModulesFor("./js/shell.js").Should().OnlyContain(m => m.Disposed, "every reference the component took is released");
    }

    [Fact]
    public async Task MainLayout_DisposesItsShellModule()
    {
        Render<MainLayout>();
        _js.ModulesFor("./js/shell.js").Should().NotBeEmpty().And.OnlyContain(m => !m.Disposed);

        await DisposeComponentsAsync();

        _js.ModulesFor("./js/shell.js").Should().OnlyContain(m => m.Disposed, "every reference the component took is released");
    }

    [Fact]
    public async Task WhenTheCircuitIsAlreadyGone_DisposingTheComponentsStillSucceeds()
    {
        _js.DisposeFailure = new JSDisconnectedException("the circuit is gone");
        Render<PostBody>(p => p.Add(x => x.Markdown, new RenderedMarkdown("<p>x</p>", [])));
        Render<ThemeSwitch>();

        var dispose = async () => await DisposeComponentsAsync();

        await dispose.Should().NotThrowAsync();
        _js.ModulesFor("./js/post.js").Should().NotBeEmpty().And.OnlyContain(m => m.DisposeAttempted, "the dispose was tried and its disconnect swallowed");
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        private readonly Dictionary<string, List<RecordingModule>> _modules = [];

        public Exception? DisposeFailure { get; set; }

        /// <summary>Every reference imported for the path: each import is a new reference, as in the browser.</summary>
        public IReadOnlyList<RecordingModule> ModulesFor(string path) => _modules.TryGetValue(path, out var list) ? list : [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import" && typeof(TValue) == typeof(IJSObjectReference))
            {
                var path = (string)args![0]!;
                var module = new RecordingModule(this);
                if (!_modules.TryGetValue(path, out var list))
                {
                    _modules[path] = list = [];
                }

                list.Add(module);

                return ValueTask.FromResult((TValue)(object)module);
            }

            return ValueTask.FromResult(default(TValue)!);
        }
    }

    private sealed class RecordingModule(RecordingJsRuntime runtime) : IJSObjectReference
    {
        public bool Disposed { get; private set; }

        public bool DisposeAttempted { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask DisposeAsync()
        {
            DisposeAttempted = true;
            if (runtime.DisposeFailure is { } failure)
            {
                throw failure;
            }

            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
