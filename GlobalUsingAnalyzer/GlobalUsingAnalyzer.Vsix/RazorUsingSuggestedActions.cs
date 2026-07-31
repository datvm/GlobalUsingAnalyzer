using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
// TextViewRolesAttribute / PredefinedTextViewRoles live in Text.Editor (Text.UI assembly).
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GlobalUsingAnalyzer.Vsix;

/// <summary>
/// Surfaces a lightbulb action on <c>.razor</c> / <c>.cshtml</c> buffers. C# <see cref="CodeFixProvider"/>
/// exports never appear in the Razor editor lightbulb (that menu is Razor language service + content-type
/// suggested actions). This MEF part is what makes "Move to nearest _Imports.razor (GlobalUsingAnalyzer)"
/// show next to VS's built-in "Promote using directive…".
/// </summary>
[Export(typeof(ISuggestedActionsSourceProvider))]
[Name(nameof(RazorUsingSuggestedActionsSourceProvider))]
[ContentType("Razor")]
[ContentType("RazorCSharp")]
[ContentType("LegacyRazorCSharp")]
[ContentType("RazorCoreCSharp")]
[ContentType("text")] // fallback; CreateSuggestedActionsSource filters to .razor/.cshtml paths
internal sealed class RazorUsingSuggestedActionsSourceProvider : ISuggestedActionsSourceProvider
{
    private readonly ITextDocumentFactoryService _textDocumentFactory;
    private readonly SVsServiceProvider _serviceProvider;

    [ImportingConstructor]
    public RazorUsingSuggestedActionsSourceProvider(
        ITextDocumentFactoryService textDocumentFactory,
        SVsServiceProvider serviceProvider)
    {
        _textDocumentFactory = textDocumentFactory;
        _serviceProvider = serviceProvider;
    }

    public ISuggestedActionsSource CreateSuggestedActionsSource(ITextView textView, ITextBuffer textBuffer)
    {
        if (textView == null || textBuffer == null)
        {
            return null;
        }

        if (!_textDocumentFactory.TryGetTextDocument(textBuffer, out var textDocument)
            || string.IsNullOrEmpty(textDocument.FilePath))
        {
            return null;
        }

        var path = textDocument.FilePath;
        if (!RazorUsingEditor.IsAnalyzableRazorSourceFile(path))
        {
            return null;
        }

        return new RazorUsingSuggestedActionsSource(textView, textBuffer, path, _serviceProvider);
    }
}

internal sealed class RazorUsingSuggestedActionsSource : ISuggestedActionsSource
{
    private readonly ITextView _textView;
    private readonly ITextBuffer _textBuffer;
    private readonly string _filePath;
    private readonly SVsServiceProvider _serviceProvider;

    public RazorUsingSuggestedActionsSource(
        ITextView textView,
        ITextBuffer textBuffer,
        string filePath,
        SVsServiceProvider serviceProvider)
    {
        _textView = textView;
        _textBuffer = textBuffer;
        _filePath = filePath;
        _serviceProvider = serviceProvider;
    }

    public event EventHandler<EventArgs> SuggestedActionsChanged
    {
        add { }
        remove { }
    }

    public void Dispose()
    {
    }

    public bool TryGetTelemetryId(out Guid telemetryId)
    {
        telemetryId = default;
        return false;
    }

    public IEnumerable<SuggestedActionSet> GetSuggestedActions(
        ISuggestedActionCategorySet requestedActionCategories,
        SnapshotSpan range,
        CancellationToken cancellationToken)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!TryGetUsingAtCaret(out var identity) || IsInherited(identity))
        {
            return Array.Empty<SuggestedActionSet>();
        }

        var title = RazorUsingEditor.IsCshtmlFile(_filePath)
            ? "Move to nearest _ViewImports.cshtml (GlobalUsingAnalyzer)"
            : "Move to nearest _Imports.razor (GlobalUsingAnalyzer)";

        var action = new PromoteRazorUsingSuggestedAction(
            title,
            _serviceProvider,
            _filePath,
            identity);

        return new[]
        {
            new SuggestedActionSet(
                PredefinedSuggestedActionCategoryNames.Refactoring,
                new ISuggestedAction[] { action },
                title: null,
                priority: SuggestedActionSetPriority.Medium,
                applicableToSpan: range),
        };
    }

    public Task<bool> HasSuggestedActionsAsync(
        ISuggestedActionCategorySet requestedActionCategories,
        SnapshotSpan range,
        CancellationToken cancellationToken)
    {
        if (!TryGetUsingAtCaret(out var identity) || IsInherited(identity))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    private bool TryGetUsingAtCaret(out string identity)
    {
        identity = null;

        var snapshot = _textBuffer.CurrentSnapshot;
        var caret = _textView.Caret.Position.BufferPosition;
        if (caret.Position < 0 || caret.Position > snapshot.Length)
        {
            return false;
        }

        var line = snapshot.GetLineFromPosition(caret.Position);
        var lineText = line.GetText();
        if (!UsingSpec.TryParseRazorUsingLine(lineText, out var spec)
            || string.IsNullOrEmpty(spec.Include))
        {
            return false;
        }

        identity = spec.Identity;
        return true;
    }

    private bool IsInherited(string identity)
    {
        if (!UsingSpec.TryParseIdentity(identity, out var spec))
        {
            return false;
        }

        try
        {
            var projectDir = FindProjectDirectory(_filePath);
            if (string.IsNullOrEmpty(projectDir) || !Directory.Exists(projectDir))
            {
                return false;
            }

            var importsName = RazorUsingEditor.GetImportsFileNameForSource(_filePath);
            var importsContents = new List<KeyValuePair<string, string>>();

            foreach (var path in Directory.EnumerateFiles(projectDir, importsName, SearchOption.AllDirectories))
            {
                if (path.IndexOf($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf($"{Path.AltDirectorySeparatorChar}bin{Path.AltDirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf($"{Path.AltDirectorySeparatorChar}obj{Path.AltDirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                try
                {
                    importsContents.Add(new KeyValuePair<string, string>(path, File.ReadAllText(path)));
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            var inherited = RazorUsingEditor.CollectInheritedUsings(_filePath, importsContents);
            return inherited.Contains(spec);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string FindProjectDirectory(string sourcePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly).Any())
                {
                    return dir;
                }

                dir = Path.GetDirectoryName(dir);
            }
        }
        catch (Exception)
        {
        }

        return null;
    }
}

internal sealed class PromoteRazorUsingSuggestedAction : ISuggestedAction
{
    private readonly string _title;
    private readonly SVsServiceProvider _serviceProvider;
    private readonly string _filePath;
    private readonly string _identity;

    public PromoteRazorUsingSuggestedAction(
        string title,
        SVsServiceProvider serviceProvider,
        string filePath,
        string identity)
    {
        _title = title;
        _serviceProvider = serviceProvider;
        _filePath = filePath;
        _identity = identity;
    }

    public string DisplayText => _title;

    public bool HasActionSets => false;

    public bool HasPreview => false;

    public string IconAutomationText => null;

    public string InputGestureText => null;

    public ImageMoniker IconMoniker => default;

    public void Dispose()
    {
    }

    public bool TryGetTelemetryId(out Guid telemetryId)
    {
        telemetryId = default;
        return false;
    }

    public Task<IEnumerable<SuggestedActionSet>> GetActionSetsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Enumerable.Empty<SuggestedActionSet>());

    public Task<object> GetPreviewAsync(CancellationToken cancellationToken) =>
        Task.FromResult<object>(null);

    public void Invoke(CancellationToken cancellationToken)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var componentModel = _serviceProvider.GetService(typeof(SComponentModel)) as IComponentModel;
            var workspace = componentModel?.GetService<VisualStudioWorkspace>();
            if (workspace == null)
            {
                return;
            }

            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await GlobalUsingAnalyzerCodeFixProvider.ApplyPromoteRazorUsingFromEditorAsync(
                        workspace,
                        _filePath,
                        _identity,
                        cancellationToken)
                    .ConfigureAwait(true);
            });
        }
        catch (Exception)
        {
            // Never crash the lightbulb host.
        }
    }
}
