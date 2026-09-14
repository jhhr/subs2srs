//  Copyright (C) 2009-2016 Christopher Brochtrup
//  Copyright (C) 2026 fkzys and contributors
//
//  This file is part of subs2srs.
//
//  subs2srs is free software: you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  subs2srs is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with subs2srs.  If not, see <http://www.gnu.org/licenses/>.
//
//////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using SysPath = System.IO.Path;

namespace subs2srs
{
    /// <summary>
    /// Preview window — GTK4/Gir.Core port.
    ///
    /// GTK4 removed TreeView/ListStore. This port uses Gtk.ColumnView
    /// with resizable columns (via P/Invoke — gir.core 0.7.0 has managed
    /// wrappers but they produced unpredictable drag-resize behavior),
    /// backed by a Gio.ListStore of dummy StringObjects and a parallel
    /// List&lt;PreviewItem&gt;.
    ///
    /// Column resize works correctly only when fixed_width and expand
    /// are never combined on the same column. Columns with fixed_width
    /// are resizable; the last column uses expand to fill remaining space.
    ///
    /// Snapshot preview uses Gtk.Picture (replaces Gtk.Image with Pixbuf).
    ///
    /// Snippet grouping: the list stays one row per subtitle line; the
    /// grouping is drawn on top (a coloured band and bracket in the leading
    /// "Group" column plus the trimmed duration of the snippet). Editing is
    /// three per-line actions (attach above, attach below, detach) reachable
    /// by buttons, configurable keys and drag-and-drop onto a neighbour, all
    /// routed through <see cref="GroupingEditor"/>. Go reuses the edited lines.
    /// </summary>
    public class DialogPreview : Gtk.Window
    {
        // CSS class names for row background styling
        private const string RowActiveCss = "preview-row-active";
        private const string RowInactiveCss = "preview-row-inactive";
        private const string BandACss = "preview-band-a";
        private const string BandBCss = "preview-band-b";
        private const string WarnCss = "color: #FF0000;";

        // Widgets
        private Gtk.DropDown _comboEp;
        private Gtk.StringList _epModel;
        private Gtk.ColumnView _columnView;
        private Gio.ListStore _store;
        private List<PreviewItem> _items = new();
        private Gtk.MultiSelection _selection;
        private Gtk.Entry _txtS1, _txtS2, _txtFind;
        private Gtk.Label _lblTime;
        private Gtk.Picture _imgSnap;
        private Gtk.CheckButton _chkSnap;
        private Gtk.Button _btnAudio, _btnGo, _btnSaveValidation;
        private Gtk.Button _btnRegroupAi, _btnCancel;
        private Gtk.Box _rowActions1, _rowActions3, _rowBottom;
        private PProgress _reporter;
        // Written by the worker thread during an AI pass, read on the GTK thread after the await.
        private string _aiMessage;
        private bool _aiFailed;
        private Gtk.Button _btnUndo, _btnRedo, _btnNextDiff;
        private Gtk.Label _lblGroupStatus;
        private Gtk.Label _lblEpL, _lblEpA, _lblEpI, _lblEpC, _lblTL, _lblTA, _lblTI;
        private Gtk.ProgressBar _progress;

        // State
        private WorkerVars _wv;
        private InfoCombined _cur;
        private bool _guard, _changed;
        private bool _running;
        // Path to the current snapshot file for opening in external viewer
        private string _currentSnapshotPath;
        private bool _destroyed;

        // Grouping editor for the episode currently shown
        private GroupingEditor _editor;
        private int _editorEp = -1;
        private List<KeyBinding> _keysAbove = new(), _keysBelow = new(), _keysDetach = new();
        private readonly Dictionary<string, uint> _keyvalCache = new();

        /// <summary>Whether this window has been destroyed or hidden permanently.</summary>
        public bool IsDestroyed => _destroyed;

        /// <summary>True while the preview is being generated.</summary>
        internal bool IsRunning => _running;

        /// <summary>Text of the preview's progress bar ("Preview ready" once loaded); for tests.</summary>
        internal string ProgressText => _progress?.GetText() ?? "";
        internal string GroupStatusText => _lblGroupStatus?.GetText() ?? "";
        /// <summary>Tooltip text of the Group column for a row (the grouper's note), "" when none.</summary>
        internal string GroupNoteAt(int pos) => pos >= 0 && pos < _items.Count ? _items[pos].GroupNote : "";
        internal void ClickRegroupAi() => OnRegroupAi(_btnRegroupAi, EventArgs.Empty);
        internal bool CancelButtonSensitive => _btnCancel?.GetSensitive() ?? false;

        /// <summary>The parsed, filtered and edited lines plus join vectors; what Go should generate.</summary>
        public WorkerVars PreviewVars => _wv;

        /// <summary>The grouping editor of the episode currently shown (for tests).</summary>
        internal GroupingEditor CurrentEditor => _editor;

        public event EventHandler RefreshSettings;

        /// <summary>
        /// Raised when the user clicks Go in Preview.
        /// MainWindow subscribes and triggers its own Go logic.
        /// </summary>
        public event EventHandler GoRequested;

        public DialogPreview() : base()
        {
            SetTitle("Preview");
            SetDefaultSize(1100, 780);

            // Hide instead of destroying on close, except when CleanupAndDestroy
            // asked for a real destroy (app exit / owner disposing the preview).
            OnCloseRequest += (s, e) =>
            {
                if (_destroyed)
                    return false; // allow destruction
                SetVisible(false);
                return true; // prevent destruction
            };

            BuildUI();
        }

        public void StartPreview()
        {
            _destroyed = false;
            // Reset running flag in case previous run was interrupted by hide
            _running = false;
            LoadKeyBindings();
            Show();
            PopulateEpCombo();
            RunPreviewAsync();
        }

        /// <summary>
        /// Clean up temp files and dispose snapshot texture.
        /// Called internally and by MainWindow on application exit.
        /// </summary>
        public void CleanupAndDestroy()
        {
            _destroyed = true;
            Cleanup();
            Close();
        }

        private void Cleanup()
        {
            // Clear snapshot picture
            try { _imgSnap?.SetPaintable(null); } catch { }

            if (_wv?.MediaDir != null && Directory.Exists(_wv.MediaDir))
                try { Directory.Delete(_wv.MediaDir, true); } catch { }
        }

        // ── UI ──────────────────────────────────────────────────────────────

        private void BuildUI()
        {
            var vbox = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
            vbox.SetMarginTop(6);
            vbox.SetMarginBottom(6);
            vbox.SetMarginStart(6);
            vbox.SetMarginEnd(6);

            var mainPane = Gtk.Paned.New(Gtk.Orientation.Vertical);
            mainPane.SetVexpand(true);
            mainPane.SetHexpand(true);

            // Top bar
            var top = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
            top.Append(Gtk.Label.New("Episode:"));
            _epModel = Gtk.StringList.New(Array.Empty<string>());
            _comboEp = Gtk.DropDown.New(_epModel, null);
            _comboEp.OnNotify += (s, e) =>
            {
                if (e.Pspec.GetName() == "selected") OnEpisodeChanged();
            };
            top.Append(_comboEp);
            var btnRegen = Gtk.Button.NewWithLabel("Regenerate");
            btnRegen.OnClicked += OnRegenClicked;
            top.Append(btnRegen);
            vbox.Append(top);

            // ColumnView with resizable columns — replaces ListView + manual header.
            // Each column gets its own SignalListItemFactory via CreateColumn().
            //
            // Layout strategy to avoid broken drag-resize:
            //   - Group, Gap, Subs1, Subs2, Start, End: fixed_width + resizable, NO expand
            //   - Duration (last column): expand=true, NO fixed_width
            // This ensures the layout engine does not fight user drag.
            _store = Gio.ListStore.New(Gtk.StringObject.GetGType());
            _selection = Gtk.MultiSelection.New(_store);
            // MultiSelection emits "selection-changed" signal, not "notify::selected"
            _selection.OnSelectionChanged += (s, e) =>
            {
                OnSelChanged();
            };

            _columnView = Gtk.ColumnView.New(_selection);
            _columnView.SetVexpand(true);
            _columnView.SetHexpand(true);
            // Show built-in column headers; disable separators for zero-gap look
            _columnView.SetShowColumnSeparators(false);
            _columnView.SetShowRowSeparators(false);

            // Fixed-width resizable columns (no expand)
            var colGroup = CreateColumn("Group", 70,
                (item) => item.GroupText, ellipsize: false, groupColumn: true);
            var colGap = CreateColumn("Gap", 55,
                (item) => item.GapText, ellipsize: false);
            var colS1 = CreateColumn("Subs1", 280,
                (item) => item.Subs1Text, ellipsize: true);
            var colS2 = CreateColumn("Subs2", 200,
                (item) => item.Subs2Text, ellipsize: true);
            var colStart = CreateColumn("Start", 110,
                (item) => item.StartText, ellipsize: false);
            var colEnd = CreateColumn("End", 110,
                (item) => item.EndText, ellipsize: false);

            // Last column: expand to fill remaining space, no fixed_width
            var colDur = CreateExpandColumn("Duration",
                (item) => item.DurText);

            _columnView.AppendColumn(colGroup);
            _columnView.AppendColumn(colGap);
            _columnView.AppendColumn(colS1);
            _columnView.AppendColumn(colS2);
            _columnView.AppendColumn(colStart);
            _columnView.AppendColumn(colEnd);
            _columnView.AppendColumn(colDur);

            // Keyboard shortcuts for the grouping actions (only while the list has focus,
            // so single-letter bindings never fire inside the text entries).
            var keys = Gtk.EventControllerKey.New();
            keys.OnKeyPressed += (c, a) => OnListKeyPressed(a.Keyval, a.State);
            _columnView.AddController(keys);

            // Wrap ColumnView in ScrolledWindow
            var sw = Gtk.ScrolledWindow.New();
            sw.SetChild(_columnView);
            sw.SetVexpand(true);
            // Ensure the list keeps space — minimum height
            sw.SetSizeRequest(-1, 250);

            mainPane.SetStartChild(sw);

            // === Bottom detail panel — fixed size, never expands ===
            var detailBox = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
            detailBox.SetVexpand(false);
            detailBox.SetValign(Gtk.Align.End);

            // Text fields grid
            var pg = Gtk.Grid.New();
            pg.SetRowSpacing(4);
            pg.SetColumnSpacing(6);
            pg.SetVexpand(false);

            var lblS1 = Gtk.Label.New("Subs1:");
            lblS1.SetHalign(Gtk.Align.End);
            pg.Attach(lblS1, 0, 0, 1, 1);
            _txtS1 = Gtk.Entry.New();
            _txtS1.SetHexpand(true);
            _txtS1.OnChanged += OnS1Changed;
            pg.Attach(_txtS1, 1, 0, 1, 1);

            var lblS2 = Gtk.Label.New("Subs2:");
            lblS2.SetHalign(Gtk.Align.End);
            pg.Attach(lblS2, 0, 1, 1, 1);
            _txtS2 = Gtk.Entry.New();
            _txtS2.SetHexpand(true);
            _txtS2.OnChanged += OnS2Changed;
            pg.Attach(_txtS2, 1, 1, 1, 1);

            _lblTime = Gtk.Label.New("");
            _lblTime.SetHalign(Gtk.Align.Start);
            pg.Attach(_lblTime, 1, 2, 1, 1);

            // Snapshot row: checkbox on the left, picture in a clipping container
            _chkSnap = Gtk.CheckButton.NewWithLabel("Snapshot preview");
            _chkSnap.SetActive(true);
            _chkSnap.SetValign(Gtk.Align.Start);
            pg.Attach(_chkSnap, 0, 3, 1, 1);

            // Picture inside a ScrolledWindow that acts as a fixed-size viewport.
            // ScrolledWindow respects size request and clips content,
            // preventing Picture from inflating the grid.
            _imgSnap = Gtk.Picture.New();
            _imgSnap.SetContentFit(Gtk.ContentFit.Contain);
            _imgSnap.SetCanShrink(true);
            _imgSnap.SetHalign(Gtk.Align.Start);
            _imgSnap.SetValign(Gtk.Align.Start);
            _imgSnap.SetVexpand(false);
            _imgSnap.SetHexpand(false);
            // Cap the picture's natural size request so it does not inflate the panel
            _imgSnap.SetSizeRequest(32, 32);

            // Click gesture to open snapshot in external viewer
            var snapClick = Gtk.GestureClick.New();
            snapClick.SetButton(1); // primary button only
            snapClick.OnReleased += OnSnapshotClicked;
            _imgSnap.AddController(snapClick);
            _imgSnap.SetCursor(Gdk.Cursor.NewFromName("pointer", null));

            pg.Attach(_imgSnap, 1, 3, 1, 1);

            detailBox.Append(pg);
            detailBox.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));

            // Action buttons row 1: selection helpers, then activate/deactivate
            var ab1 = _rowActions1 = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
            AppendBtn(ab1, "Select All", OnSelectAll);
            AppendBtn(ab1, "Select None", OnSelectNone);
            AppendBtn(ab1, "Invert", OnInvertSelection);
            ab1.Append(Gtk.Separator.New(Gtk.Orientation.Vertical));
            AppendBtn(ab1, "Activate", OnActivate);
            AppendBtn(ab1, "Deactivate", OnDeactivate);
            detailBox.Append(ab1);

            // Action buttons row 2: snippet grouping
            var ab3 = _rowActions3 = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
            ab3.Append(Gtk.Label.New("Snippet:"));
            var bAbove = Gtk.Button.NewWithLabel("Attach ↑");
            bAbove.SetTooltipText("Attach the selected line to the line above (drag it up, or use the key from Preferences)");
            bAbove.OnClicked += (s, e) => ApplyToSelected(JoinAction.AttachAbove);
            ab3.Append(bAbove);
            var bBelow = Gtk.Button.NewWithLabel("Attach ↓");
            bBelow.SetTooltipText("Attach the selected line to the line below (drag it down, or use the key from Preferences)");
            bBelow.OnClicked += (s, e) => ApplyToSelected(JoinAction.AttachBelow);
            ab3.Append(bBelow);
            var bDetach = Gtk.Button.NewWithLabel("Detach");
            bDetach.SetTooltipText("Make the selected line a card of its own (drop it onto itself, or use the key from Preferences)");
            bDetach.OnClicked += (s, e) => ApplyToSelected(JoinAction.Detach);
            ab3.Append(bDetach);
            ab3.Append(Gtk.Separator.New(Gtk.Orientation.Vertical));
            AppendBtn(ab3, "Regroup (rules)", OnRegroupRules);
            _btnRegroupAi = Gtk.Button.NewWithLabel("Regroup (AI)");
            _btnRegroupAi.SetTooltipText("Ask the model from Advanced Subtitle Options to group this episode again "
                + "(ignores the cache). The token and cost estimate is shown on the progress bar before the call.");
            _btnRegroupAi.OnClicked += OnRegroupAi;
            ab3.Append(_btnRegroupAi);
            AppendBtn(ab3, "Ungroup all", OnUngroupAll);
            ab3.Append(Gtk.Separator.New(Gtk.Orientation.Vertical));
            _btnUndo = Gtk.Button.NewWithLabel("Undo");
            _btnUndo.OnClicked += (s, e) => { if (_editor != null && _editor.Undo()) AfterGroupingChange(); };
            ab3.Append(_btnUndo);
            _btnRedo = Gtk.Button.NewWithLabel("Redo");
            _btnRedo.OnClicked += (s, e) => { if (_editor != null && _editor.Redo()) AfterGroupingChange(); };
            ab3.Append(_btnRedo);
            _btnNextDiff = Gtk.Button.NewWithLabel("Next diff");
            _btnNextDiff.SetTooltipText("Jump to the next line where your grouping differs from the proposal");
            _btnNextDiff.OnClicked += OnNextDisagreement;
            ab3.Append(_btnNextDiff);
            _lblGroupStatus = Gtk.Label.New("");
            _lblGroupStatus.SetHalign(Gtk.Align.Start);
            _lblGroupStatus.SetEllipsize(Pango.EllipsizeMode.End);
            _lblGroupStatus.SetHexpand(true);
            ab3.Append(_lblGroupStatus);
            detailBox.Append(ab3);

            // Find + audio
            var ab2 = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
            ab2.Append(Gtk.Label.New("Find:"));
            _txtFind = Gtk.Entry.New();
            _txtFind.SetWidthChars(25);
            _txtFind.OnActivate += (s, e) => FindNext();
            ab2.Append(_txtFind);
            AppendBtn(ab2, "Find Next", (s, e) => FindNext());
            ab2.Append(Gtk.Separator.New(Gtk.Orientation.Vertical));
            _btnAudio = Gtk.Button.NewWithLabel("Preview Audio");
            _btnAudio.SetTooltipText("Play the selected line, or its whole snippet with dead space removed");
            _btnAudio.OnClicked += OnPreviewAudio;
            ab2.Append(_btnAudio);
            detailBox.Append(ab2);

            // Stats
            var sf = Gtk.Frame.New("Statistics");
            var sg = Gtk.Grid.New();
            sg.SetRowSpacing(2);
            sg.SetColumnSpacing(8);
            sg.SetMarginTop(4);
            sg.SetMarginBottom(4);
            sg.SetMarginStart(4);
            sg.SetMarginEnd(4);
            sg.Attach(Gtk.Label.New("Episode —"), 0, 0, 1, 1);
            sg.Attach(Gtk.Label.New("Lines:"), 1, 0, 1, 1);
            _lblEpL = Gtk.Label.New("0"); sg.Attach(_lblEpL, 2, 0, 1, 1);
            sg.Attach(Gtk.Label.New("Active:"), 3, 0, 1, 1);
            _lblEpA = Gtk.Label.New("0"); sg.Attach(_lblEpA, 4, 0, 1, 1);
            sg.Attach(Gtk.Label.New("Inactive:"), 5, 0, 1, 1);
            _lblEpI = Gtk.Label.New("0"); sg.Attach(_lblEpI, 6, 0, 1, 1);
            sg.Attach(Gtk.Label.New("Cards:"), 7, 0, 1, 1);
            _lblEpC = Gtk.Label.New("0"); sg.Attach(_lblEpC, 8, 0, 1, 1);
            sg.Attach(Gtk.Label.New("Total —"), 0, 1, 1, 1);
            sg.Attach(Gtk.Label.New("Lines:"), 1, 1, 1, 1);
            _lblTL = Gtk.Label.New("0"); sg.Attach(_lblTL, 2, 1, 1, 1);
            sg.Attach(Gtk.Label.New("Active:"), 3, 1, 1, 1);
            _lblTA = Gtk.Label.New("0"); sg.Attach(_lblTA, 4, 1, 1, 1);
            sg.Attach(Gtk.Label.New("Inactive:"), 5, 1, 1, 1);
            _lblTI = Gtk.Label.New("0"); sg.Attach(_lblTI, 6, 1, 1, 1);
            sf.SetChild(sg);
            detailBox.Append(sf);

            _progress = Gtk.ProgressBar.New();
            _progress.SetShowText(true);
            _progress.SetText("");
            _progress.SetHexpand(true);
            _progress.SetValign(Gtk.Align.Center);
            var progressRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
            progressRow.Append(_progress);
            _btnCancel = Gtk.Button.NewWithLabel("Cancel");
            _btnCancel.SetTooltipText("Stop the running preview or AI grouping");
            _btnCancel.SetSensitive(false);
            _btnCancel.OnClicked += (s, e) => { if (_reporter != null) _reporter.Cancel = true; };
            progressRow.Append(_btnCancel);
            detailBox.Append(progressRow);

            // Bottom buttons
            var bot = _rowBottom = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
            bot.SetHalign(Gtk.Align.End);
            _btnSaveValidation = Gtk.Button.NewWithLabel("Save as validation");
            _btnSaveValidation.SetTooltipText("Write one JSON file per episode with the kept lines and the current grouping (for evaluating groupers)");
            _btnSaveValidation.OnClicked += OnSaveValidation;
            bot.Append(_btnSaveValidation);
            var btnClose = Gtk.Button.NewWithLabel("Close");
            btnClose.SetSizeRequest(100, -1);
            btnClose.OnClicked += (s, e) => SetVisible(false);
            bot.Append(btnClose);
            _btnGo = Gtk.Button.NewWithLabel("Go!");
            _btnGo.SetSizeRequest(100, -1);
            _btnGo.OnClicked += OnGoClicked;
            bot.Append(_btnGo);
            detailBox.Append(bot);

            mainPane.SetEndChild(detailBox);

            vbox.Append(mainPane);

            // Allow both children to shrink freely so SetPosition is honored
            mainPane.SetShrinkStartChild(true);
            mainPane.SetShrinkEndChild(true);

            bool panedInitialized = false;
            mainPane.OnMap += (s, e) =>
            {
                if (panedInitialized) return;
                panedInitialized = true;
                // OnMap fires when widget is realized; schedule after first full layout
                GLib.Functions.TimeoutAdd(0, 50, () =>
                {
                    int total = mainPane.GetAllocatedHeight();
                    if (total <= 0) return true; // keep waiting
                    // Bottom panel gets ~510px
                    int pos = total - 510;
                    if (pos < 150) pos = 150;
                    mainPane.SetPosition(pos);
                    return false; // stop timer
                });
            };

            // Register CSS provider for row background, selection styling,
            // and zero-gap between ColumnView cells
            var cssProvider = Gtk.CssProvider.New();
            cssProvider.LoadFromString(
                // Normal state
                ".preview-row-active  { background-color: #FFFFF5; color: #1A1A1A; }" +
                ".preview-row-inactive { background-color: #FFB6C1; color: #1A1A1A; }" +
                ".preview-row-active  label { color: inherit; }" +
                ".preview-row-inactive label { color: inherit; }" +
                // Snippet bands (leading column): alternate per snippet
                ".preview-band-a { background-color: #C9E2FF; color: #1A1A1A; font-weight: 700; }" +
                ".preview-band-b { background-color: #FFE0B8; color: #1A1A1A; font-weight: 700; }" +
                // Selected state — darker shade + visible outline
                "columnview listview > row:selected .preview-row-active  " +
                    "{ background-color: #F0F0E5; outline: 2px solid #3584E4; outline-offset: -2px; }" +
                "columnview listview > row:selected .preview-row-inactive " +
                    "{ background-color: #E8849A; outline: 2px solid #3584E4; outline-offset: -2px; }" +
                "columnview listview > row:selected .preview-band-a { background-color: #A9CCF5; }" +
                "columnview listview > row:selected .preview-band-b { background-color: #F5CB9A; }" +
                "columnview listview > row:selected .preview-row-active  label { color: inherit; }" +
                "columnview listview > row:selected .preview-row-inactive label { color: inherit; }" +
                // Zero-gap: remove padding/margin on ColumnView cells
                "columnview > listview > row > cell { padding: 0; margin: 0; }" +
                // Compact column headers with no border-radius for seamless look
                "columnview > header > button { padding: 2px 6px; margin: 0; " +
                    "border-radius: 0; min-height: 0; }");
            Gtk.StyleContext.AddProviderForDisplay(
                Gdk.Display.GetDefault()!,
                cssProvider,
                800); // Higher priority to override theme defaults

            // Defer per-widget header styling until ColumnView children exist.
            // The widget tree is only fully built after the first layout pass.
            GLib.Functions.IdleAdd(0, () =>
            {
                GtkColumnViewHelper.StyleColumnViewHeaders(
                    _columnView,
                    "color: @theme_fg_color; opacity: 1.0; font-weight: 700;");
                return false; // run once
            });

            SetChild(vbox);
            UpdateGroupingButtons();
        }

        /// <summary>
        /// Create a fixed-width, resizable column (no expand).
        /// Each cell renders a Label whose text comes from textSelector.
        /// The row-level CSS class (active/inactive) is applied to a
        /// wrapper Box so the background fills the entire cell area.
        /// Every cell is also a drag source and a drop target for the
        /// grouping gestures (drag a row onto its neighbour).
        ///
        /// Important: this column does NOT use expand, only fixed_width.
        /// Mixing expand + fixed_width causes broken drag-resize behavior.
        /// </summary>
        private Gtk.ColumnViewColumn CreateColumn(
            string title, int fixedWidth,
            Func<PreviewItem, string> textSelector, bool ellipsize, bool groupColumn = false)
        {
            var factory = Gtk.SignalListItemFactory.New();

            factory.OnSetup += (f, args) =>
            {
                var listItem = (Gtk.ListItem)args.Object;
                // Wrap label in a box so CSS background fills the entire cell
                var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
                box.SetHexpand(true);

                var lbl = Gtk.Label.New("");
                lbl.SetHalign(Gtk.Align.Start);
                lbl.SetHexpand(true);
                lbl.SetMarginStart(4);
                lbl.SetMarginEnd(4);
                if (ellipsize)
                    lbl.SetEllipsize(Pango.EllipsizeMode.End);

                box.Append(lbl);
                listItem.SetChild(box);
                AttachRowDragAndDrop(box, listItem);
            };

            factory.OnBind += (f, args) =>
            {
                var listItem = (Gtk.ListItem)args.Object;
                uint pos = listItem.GetPosition();
                if (pos >= _items.Count) return;

                var item = _items[(int)pos];
                var box = (Gtk.Box)listItem.GetChild();
                if (box == null) return;

                // Apply background color based on active/inactive state (or the snippet band)
                box.RemoveCssClass(RowActiveCss);
                box.RemoveCssClass(RowInactiveCss);
                box.RemoveCssClass(BandACss);
                box.RemoveCssClass(BandBCss);
                if (groupColumn && item.Band == 1) box.AddCssClass(BandACss);
                else if (groupColumn && item.Band == 2) box.AddCssClass(BandBCss);
                else box.AddCssClass(item.IsActive ? RowActiveCss : RowInactiveCss);

                var lbl = (Gtk.Label)box.GetFirstChild();
                lbl.SetText(textSelector(item));
                if (groupColumn)
                    box.SetTooltipText(string.IsNullOrEmpty(item.GroupNote) ? null : item.GroupNote);
            };

            var col = Gtk.ColumnViewColumn.New(title, factory);

            // Fixed width + resizable, NO expand — clean drag behavior
            GtkColumnViewHelper.SetResizable(col, true);
            GtkColumnViewHelper.SetFixedWidth(col, fixedWidth);
            GtkColumnViewHelper.SetExpand(col, false);

            return col;
        }

        /// <summary>
        /// Create the last column that expands to fill remaining width.
        /// No fixed_width is set — only expand=true. This column is also
        /// resizable but will absorb/release space as the window resizes.
        /// </summary>
        private Gtk.ColumnViewColumn CreateExpandColumn(
            string title, Func<PreviewItem, string> textSelector)
        {
            var factory = Gtk.SignalListItemFactory.New();

            factory.OnSetup += (f, args) =>
            {
                var listItem = (Gtk.ListItem)args.Object;
                var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
                box.SetHexpand(true);

                var lbl = Gtk.Label.New("");
                lbl.SetHalign(Gtk.Align.Start);
                lbl.SetHexpand(true);
                lbl.SetMarginStart(4);
                lbl.SetMarginEnd(4);

                box.Append(lbl);
                listItem.SetChild(box);
                AttachRowDragAndDrop(box, listItem);
            };

            factory.OnBind += (f, args) =>
            {
                var listItem = (Gtk.ListItem)args.Object;
                uint pos = listItem.GetPosition();
                if (pos >= _items.Count) return;

                var item = _items[(int)pos];
                var box = (Gtk.Box)listItem.GetChild();
                if (box == null) return;

                box.RemoveCssClass(RowActiveCss);
                box.RemoveCssClass(RowInactiveCss);
                box.AddCssClass(item.IsActive ? RowActiveCss : RowInactiveCss);

                var lbl = (Gtk.Label)box.GetFirstChild();
                lbl.SetText(textSelector(item));
            };

            var col = Gtk.ColumnViewColumn.New(title, factory);

            // Expand only, no fixed_width — absorbs remaining space
            GtkColumnViewHelper.SetExpand(col, true);
            GtkColumnViewHelper.SetResizable(col, true);

            return col;
        }

        /// <summary>
        /// Drag a row onto the row above it = attach above, onto the row below =
        /// attach below, onto itself = detach. The payload is the source row
        /// position; the target is the position the cell is bound to at drop time.
        /// </summary>
        private void AttachRowDragAndDrop(Gtk.Box box, Gtk.ListItem listItem)
        {
            try
            {
                var drag = Gtk.DragSource.New();
                drag.SetActions(Gdk.DragAction.Move);
                drag.OnPrepare += (src, a) =>
                {
                    uint pos = listItem.GetPosition();
                    if (pos >= _items.Count || !_items[(int)pos].IsActive) return null;
                    return Gdk.ContentProvider.NewForValue(new GObject.Value(pos.ToString()));
                };
                box.AddController(drag);

                var drop = Gtk.DropTarget.New(GObject.Type.String, Gdk.DragAction.Move);
                drop.OnDrop += (tgt, a) =>
                {
                    string payload = null;
                    try { payload = a.Value?.GetString(); } catch { }
                    if (!int.TryParse(payload, out int srcPos)) return false;
                    return HandleRowDrop(srcPos, (int)listItem.GetPosition());
                };
                box.AddController(drop);
            }
            catch (Exception ex)
            {
                // Drag-and-drop is a convenience; the buttons and keys still work.
                Logger.Instance.info("Preview: drag-and-drop unavailable: " + ex.Message);
            }
        }

        /// <summary>
        /// Helper: create a labelled button, attach handler, append to box.
        /// Gir.Core Gtk.Button.OnClicked uses EventHandler&lt;EventArgs&gt;.
        /// </summary>
        private void AppendBtn(Gtk.Box box, string label,
            GObject.SignalHandler<Gtk.Button> handler)
        {
            var b = Gtk.Button.NewWithLabel(label);
            b.OnClicked += handler;
            box.Append(b);
        }

        // ── PREVIEW GENERATION ──────────────────────────────────────────────

        private void PopulateEpCombo()
        {
            _guard = true;
            uint prev = _comboEp.GetSelected();
            // Use already-truncated Files array to respect Episode End # limit
            int n = Settings.Instance.Subs[0].Files.Length;
            int first = Settings.Instance.EpisodeStartNumber;
            var names = new string[n];
            for (int i = 0; i < n; i++)
                names[i] = (i + first).ToString();
            _epModel = Gtk.StringList.New(names);
            _comboEp.SetModel(_epModel);
            _comboEp.SetSelected(
                (prev < (uint)n) ? prev : 0);
            _guard = false;
        }

        private async void RunPreviewAsync()
        {
            if (_running) return;
            _running = true;
            SetBusy(true);
            var reporter = _reporter = new PProgress(_progress, () => _destroyed);

            WorkerVars result = null;
            Exception err = null;

            try
            {
                result = await Task.Run(() => DoPreviewWork(reporter));
            }
            catch (Exception ex)
            {
                err = ex;
            }

            reporter.Stop();

            if (_destroyed) return;

            _running = false;
            _reporter = null;
            SetBusy(false);
            if (err != null)
            {
                _progress.SetText(err is OperationCanceledException ? "Cancelled" : "Error");
                _progress.SetFraction(0);
                if (!(err is OperationCanceledException))
                    UtilsMsg.showErrMsg(err.Message);
                return;
            }

            _wv = result;
            _editor = null;
            _editorEp = -1;
            int ep = (int)_comboEp.GetSelected();
            if (ep < 0) ep = 0;
            _guard = true;
            PopulateList(ep);
            _guard = false;
            UpdateStats();
            _progress.SetText("Preview ready");
            _progress.SetFraction(1.0);
            if (_aiMessage != null)
            {
                ShowGroupStatus(_aiMessage, !_aiFailed);
                _lblGroupStatus.SetTooltipText(_aiMessage);
            }

            if (_store.GetNItems() > 0)
            {
                var one = Gtk.Bitset.NewEmpty();
                one.Add(0);
                var all = Gtk.Bitset.NewRange(0, _store.GetNItems());
                _selection.SetSelection(one, all);
                // Explicitly trigger detail update for the first item
                OnSelChanged();
            }
        }

        private WorkerVars DoPreviewWork(IProgressReporter rpt)
        {
            string dir = SysPath.Combine(SysPath.GetTempPath(),
                ConstantSettings.TempPreviewDirName);
            if (Directory.Exists(dir))
                try { Directory.Delete(dir, true); } catch { }
            Directory.CreateDirectory(dir);

            var wv = new WorkerVars(null, dir, WorkerVars.SubsProcessingType.Preview);
            var sw = new WorkerSubs();

            var c = sw.combineAllSubs(wv, rpt);
            if (c == null) throw new OperationCanceledException();
            wv.CombinedAll = c;

            int total = 0;
            foreach (var a in wv.CombinedAll) total += a.Count;
            if (total == 0)
                throw new Exception("No lines could be parsed from subtitle files.");

            c = sw.inactivateLines(wv, rpt);
            if (c == null) throw new OperationCanceledException();
            wv.CombinedAll = c;

            // Propose a grouping (rules, or nothing); Go reuses these vectors,
            // so whatever the user edits here is what gets generated.
            rpt.UpdateProgress(0, "Grouping lines into snippets");
            SnippetMode mode = Settings.Instance.Snippets.Mode;
            var limits = SnippetLimits.FromSettings();
            var options = RuleGrouperOptions.FromSettings();
            wv.Joins = new List<bool[]>();
            wv.ProposedJoins = mode == SnippetMode.Off ? null : new List<bool[]>();
            wv.ProposalProducer = mode == SnippetMode.Off ? null : "rules";
            _aiMessage = null;
            _aiFailed = false;

            for (int ep = 0; ep < wv.CombinedAll.Count; ep++)
            {
                var lines = wv.CombinedAll[ep];
                bool[] joins = null;
                if (mode == SnippetMode.AI)
                    joins = ProposeAiJoins(wv, ep, rpt, forceRefresh: false);
                // Rules when the mode says so, or as the fallback after a failed AI pass
                joins ??= ProposeJoins(lines, mode == SnippetMode.AI ? SnippetMode.Rules : mode, limits, options);
                wv.Joins.Add(joins);
                wv.ProposedJoins?.Add((bool[])joins.Clone());
                if (rpt.Cancel) throw new OperationCanceledException();
            }
            rpt.UpdateProgress(100, "Grouping lines into snippets");

            return wv;
        }

        /// <summary>
        /// Run the AI grouper for one episode on the worker thread. The token/cost estimate goes to
        /// the progress bar and the log before the call. Returns null when the provider failed
        /// (the error text is kept for the status label) so the caller falls back to the rules;
        /// a cancel propagates as OperationCanceledException.
        /// </summary>
        private bool[] ProposeAiJoins(WorkerVars wv, int ep, IProgressReporter rpt, bool forceRefresh)
        {
            var lines = wv.CombinedAll[ep];
            var limits = SnippetLimits.FromSettings();
            var aiOptions = AiGroupingOptions.FromSettings(forceRefresh);
            aiOptions.ProgressLabel = wv.CombinedAll.Count == 1
                ? "AI grouping" : $"AI grouping (episode {ep + 1} of {wv.CombinedAll.Count})";
            try
            {
                AiCostEstimate estimate = AiGrouper.Estimate(lines, limits, aiOptions);
                string before = $"{aiOptions.ProgressLabel}: {estimate.Describe()}";
                rpt.UpdateProgress(0, before);
                Logger.Instance.info(before);

                AiGroupingResult result = AiGrouper.Group(lines, limits, aiOptions, rpt, rpt.Token);
                bool[] joins = AiGrouper.ApplyToLines(result, lines);
                wv.ProposalProducer = "ai";
                wv.ProposalModel = result.Model;
                wv.ProposalPromptVersion = result.PromptVersion;
                _aiMessage = DescribeAiResult(result);
                _aiFailed = false;
                return joins;
            }
            catch (ProviderException ex)
            {
                Logger.Instance.info("AI grouping failed, using the rules: " + ex.Message);
                _aiMessage = ex.Message + " (grouped with the rules instead)";
                _aiFailed = true;
                return null;
            }
        }

        private static string DescribeAiResult(AiGroupingResult r)
        {
            if (r.FromCache) return $"AI grouping ({r.Model}): cached answer, no request made.";
            string text = FormattableString.Invariant(
                $"AI grouping ({r.Model}): {r.Chunks} request(s), {r.InputTokens:N0} input + {r.OutputTokens:N0} output tokens");
            if (AiPricing.Usd(r.Model, r.InputTokens, r.OutputTokens) is double usd)
                text += " (about $" + usd.ToString("0.000", CultureInfo.InvariantCulture) + ")";
            if (r.FailedChunks > 0)
                text += FormattableString.Invariant($"; {r.FailedChunks} chunk(s) fell back to the rules");
            return text + ".";
        }

        /// <summary>While the preview or an AI pass runs: list, action rows and Go are off, Cancel is on.</summary>
        private void SetBusy(bool busy)
        {
            _columnView?.SetSensitive(!busy);
            _comboEp?.SetSensitive(!busy);
            _rowActions1?.SetSensitive(!busy);
            _rowActions3?.SetSensitive(!busy);
            _rowBottom?.SetSensitive(!busy);
            _btnCancel?.SetSensitive(busy);
        }

        private async void OnRegroupAi(Gtk.Button s, EventArgs e)
        {
            if (_editor == null || _wv == null || _running) return;
            int ep = (int)_comboEp.GetSelected();
            if (ep < 0 || ep >= _wv.CombinedAll.Count) return;

            _running = true;
            SetBusy(true);
            var reporter = _reporter = new PProgress(_progress, () => _destroyed);
            bool[] joins = null;
            Exception err = null;
            try
            {
                joins = await Task.Run(() => ProposeAiJoins(_wv, ep, reporter, forceRefresh: true));
            }
            catch (Exception ex)
            {
                err = ex;
            }
            reporter.Stop();
            if (_destroyed) return;
            _running = false;
            _reporter = null;
            SetBusy(false);

            if (err != null)
            {
                _progress.SetText(err is OperationCanceledException ? "AI grouping cancelled" : "Error");
                _progress.SetFraction(0);
                if (!(err is OperationCanceledException)) UtilsMsg.showErrMsg(err.Message);
                return;
            }
            _progress.SetText("Preview ready");
            _progress.SetFraction(1.0);
            _lblGroupStatus.SetTooltipText(_aiMessage);
            if (joins == null)
            {
                ShowGroupStatus(_aiMessage ?? "AI grouping failed.", false);
                return;
            }

            _editor.Limits = SnippetLimits.FromSettings();
            _editor.SetJoins(joins);
            _wv.ProposedJoins ??= new List<bool[]>();
            while (_wv.ProposedJoins.Count < _wv.CombinedAll.Count) _wv.ProposedJoins.Add(null);
            _wv.ProposedJoins[ep] = (bool[])joins.Clone();
            ShowGroupStatus(_aiMessage, true);
            AfterGroupingChange();
        }

        /// <summary>Rules (or nothing) followed by the limit check, as a full-index join vector.</summary>
        private static bool[] ProposeJoins(List<InfoCombined> lines, SnippetMode mode,
            SnippetLimits limits, RuleGrouperOptions options)
        {
            bool[] joins = WorkerSubs.computeJoins(lines, mode, limits, options);
            int[] kept = SnippetGrouping.KeptIndices(lines);
            bool[] repaired = SnippetGrouping.Repair(lines, kept, SnippetGrouping.ProjectToKept(joins, kept), limits);
            SnippetGrouping.ApplyKeptJoins(joins, kept, repaired);
            return joins;
        }

        // ── LIST VIEW ───────────────────────────────────────────────────────

        private void PopulateList(int epIdx)
        {
            _store.RemoveAll();
            _items.Clear();
            if (_wv?.CombinedAll == null || epIdx < 0
                || epIdx >= _wv.CombinedAll.Count) return;

            EnsureEditor(epIdx);

            var arr = _wv.CombinedAll[epIdx];
            for (int i = 0; i < arr.Count; i++)
            {
                var cb = arr[i];
                string dur = FmtTime(UtilsSubs.getDurationTime(
                    cb.Subs1.StartTime, cb.Subs1.EndTime));
                if (IsLong(cb)) dur += " (Long!)";

                _items.Add(PreviewItem.Create(
                    cb.Subs1.Text, cb.Subs2.Text,
                    FmtTime(cb.Subs1.StartTime), FmtTime(cb.Subs1.EndTime),
                    dur, cb.Active, i));
            }

            ComputeGroupTexts();

            for (int i = 0; i < arr.Count; i++)
                _store.Append(Gtk.StringObject.New(""));

            UpdateGroupingButtons();
        }

        /// <summary>Create the grouping editor for an episode (once per episode per preview run).</summary>
        private void EnsureEditor(int epIdx)
        {
            if (_editor != null && _editorEp == epIdx) return;
            if (_wv?.Joins == null || epIdx >= _wv.Joins.Count) return;

            var lines = _wv.CombinedAll[epIdx];
            var editor = new GroupingEditor(lines, _wv.Joins[epIdx], SnippetLimits.FromSettings());
            int ep = epIdx;
            editor.Changed += () =>
            {
                if (_wv?.Joins != null && ep < _wv.Joins.Count)
                    _wv.Joins[ep] = editor.Joins;
            };
            _editor = editor;
            _editorEp = epIdx;
        }

        /// <summary>
        /// Fill the Group/Gap texts and the colour band of every row from the editor:
        /// a bracket (┌ │ └) with the snippet's trimmed duration on its first line,
        /// and the gap to the previous kept line.
        /// </summary>
        private void ComputeGroupTexts()
        {
            if (_editor == null)
            {
                foreach (var it in _items) { it.GroupText = ""; it.GapText = ""; it.Band = 0; }
                return;
            }

            var lines = _editor.Lines;
            int[] kept = _editor.Kept;
            bool[] joins = _editor.Joins;
            bool[] keptJoins = SnippetGrouping.ProjectToKept(joins, kept);
            var ranges = SnippetGrouping.JoinsToRanges(keptJoins, kept.Length);

            // line index -> (ordinal, first, last, durationMs)
            var info = new Dictionary<int, (int ordinal, int first, int last, int dur)>();
            for (int o = 0; o < ranges.Count; o++)
            {
                int first = kept[ranges[o].first];
                int last = kept[ranges[o].last];
                int dur = SnippetGrouping.TrimmedDurationMs(lines, first, last, _editor.Limits);
                for (int i = first; i <= last; i++)
                    if (lines[i].Active) info[i] = (o, first, last, dur);
            }

            foreach (var it in _items)
            {
                int idx = it.Index;
                it.GroupText = "";
                it.GapText = "";
                it.GroupNote = "";
                it.Band = 0;
                if (idx < 0 || idx >= lines.Count || !lines[idx].Active) continue;

                int gap = _editor.GapToPreviousKeptMs(idx);
                it.GapText = gap >= 0 ? $"{gap / 1000.0:0.00}" : "";

                if (!info.TryGetValue(idx, out var g) || g.first == g.last) continue;

                it.Band = (g.ordinal % 2 == 0) ? 1 : 2;
                it.GroupNote = lines[g.first].GroupNote ?? "";
                if (idx == g.first) it.GroupText = $"┌ {g.dur / 1000.0:0.0}s";
                else if (idx == g.last) it.GroupText = "└";
                else it.GroupText = "│";
            }
        }

        private string FmtTime(TimeSpan t) =>
            $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";

        private bool IsLong(InfoCombined cb)
        {
            var d = UtilsSubs.getDurationTime(cb.Subs1.StartTime, cb.Subs1.EndTime);
            return ConstantSettings.LongClipWarningSeconds > 0 &&
                   d.TotalMilliseconds > ConstantSettings.LongClipWarningSeconds * 1000;
        }

        // ── SELECTION ───────────────────────────────────────────────────────

        /// <summary>Row position of the last-clicked selected row, or -1.</summary>
        private int SelectedPosition()
        {
            var bitset = _selection.GetSelection();
            if (bitset == null || bitset.GetSize() == 0) return -1;
            uint pos = bitset.GetMaximum();
            return pos < _items.Count ? (int)pos : -1;
        }

        private void OnSelChanged()
        {
            if (_guard) return;

            // Show detail for the last item in the selection bitset
            int pos = SelectedPosition();
            if (pos < 0) return;

            int ep = (int)_comboEp.GetSelected();
            if (_wv?.CombinedAll == null || ep < 0
                || ep >= _wv.CombinedAll.Count) return;
            var arr = _wv.CombinedAll[ep];
            int idx = _items[pos].Index;
            if (idx < 0 || idx >= arr.Count) return;

            var comb = arr[idx];
            _cur = comb;
            _guard = true;
            _txtS1.SetText(comb.Subs1.Text);
            _txtS2.SetText(comb.Subs2.Text);
            _txtS2.SetSensitive(Settings.Instance.Subs[1].Files.Length > 0);
            _lblTime.SetText(DescribeLine(idx, comb));
            _guard = false;

            UpdateSnapshot(comb);
        }

        private string DescribeLine(int idx, InfoCombined comb)
        {
            string text = FmtTime(comb.Subs1.StartTime) + "  —  " + FmtTime(comb.Subs1.EndTime);
            var range = _editor?.SnippetRangeOf(idx);
            if (range != null && range.Value.first != range.Value.last)
            {
                int n = range.Value.last - range.Value.first + 1;
                int dur = _editor.TrimmedDurationMsOf(idx);
                text += $"   ·   snippet of {n} lines, {dur / 1000.0:0.0} s after gap removal";
            }
            return text;
        }

        private InfoCombined GetSelectedCombined()
        {
            int pos = SelectedPosition();
            if (pos < 0) return null;

            var item = _items[pos];
            int ep = (int)_comboEp.GetSelected();
            if (_wv?.CombinedAll == null || ep < 0
                || ep >= _wv.CombinedAll.Count) return null;
            var arr = _wv.CombinedAll[ep];
            int idx = item.Index;
            return idx >= 0 && idx < arr.Count ? arr[idx] : null;
        }

        // ── GROUPING EDITOR ─────────────────────────────────────────────────

        private void LoadKeyBindings()
        {
            _keysAbove = KeyBinding.ParseList(ConstantSettings.GroupingKeyAttachAbove);
            _keysBelow = KeyBinding.ParseList(ConstantSettings.GroupingKeyAttachBelow);
            _keysDetach = KeyBinding.ParseList(ConstantSettings.GroupingKeyDetach);
            _keyvalCache.Clear();
        }

        private uint ResolveKeyval(string name)
        {
            if (_keyvalCache.TryGetValue(name, out uint v)) return v;
            uint keyval = 0;
            try { keyval = Gdk.Functions.KeyvalToLower(Gdk.Functions.KeyvalFromName(name)); } catch { }
            _keyvalCache[name] = keyval;
            return keyval;
        }

        private static KeyBinding.Mods ToMods(Gdk.ModifierType state)
        {
            var m = KeyBinding.Mods.None;
            if ((state & Gdk.ModifierType.ShiftMask) != 0) m |= KeyBinding.Mods.Shift;
            if ((state & Gdk.ModifierType.ControlMask) != 0) m |= KeyBinding.Mods.Control;
            if ((state & Gdk.ModifierType.AltMask) != 0) m |= KeyBinding.Mods.Alt;
            if ((state & Gdk.ModifierType.SuperMask) != 0) m |= KeyBinding.Mods.Super;
            return m;
        }

        private bool MatchesAny(List<KeyBinding> bindings, uint keyval, KeyBinding.Mods mods)
        {
            foreach (var b in bindings)
                if (b.Matches(keyval, ResolveKeyval(b.KeyName), mods)) return true;
            return false;
        }

        /// <summary>Key press inside the list: run a grouping action if a binding matches.</summary>
        internal bool OnListKeyPressed(uint keyval, Gdk.ModifierType state)
        {
            uint lower;
            try { lower = Gdk.Functions.KeyvalToLower(keyval); } catch { lower = keyval; }
            var mods = ToMods(state);

            JoinAction? action = null;
            if (MatchesAny(_keysAbove, lower, mods)) action = JoinAction.AttachAbove;
            else if (MatchesAny(_keysBelow, lower, mods)) action = JoinAction.AttachBelow;
            else if (MatchesAny(_keysDetach, lower, mods)) action = JoinAction.Detach;
            if (action == null) return false;

            ApplyToSelected(action.Value);
            return true;
        }

        /// <summary>Drop of row <paramref name="srcPos"/> onto row <paramref name="dstPos"/>.</summary>
        internal bool HandleRowDrop(int srcPos, int dstPos)
        {
            if (srcPos < 0 || srcPos >= _items.Count || dstPos < 0 || dstPos >= _items.Count) return false;

            JoinAction action;
            if (dstPos == srcPos - 1) action = JoinAction.AttachAbove;
            else if (dstPos == srcPos + 1) action = JoinAction.AttachBelow;
            else if (dstPos == srcPos) action = JoinAction.Detach;
            else
            {
                ShowGroupStatus("Drop a line onto the line directly above or below it to attach, or onto itself to detach.", false);
                return false;
            }

            SelectRow(srcPos);
            var result = ApplyToRow(srcPos, action);
            return result.Ok;
        }

        /// <summary>Apply a grouping action to the selected row; returns the editor's verdict.</summary>
        internal JoinResult ApplyToSelected(JoinAction action)
        {
            int pos = SelectedPosition();
            if (pos < 0)
            {
                var r = JoinResult.Rejected("Select a line first.");
                ShowGroupStatus(r.Message, false);
                return r;
            }
            return ApplyToRow(pos, action);
        }

        private JoinResult ApplyToRow(int pos, JoinAction action)
        {
            if (_editor == null) return JoinResult.Rejected("No preview loaded.");
            int idx = _items[pos].Index;
            JoinResult result = _editor.Apply(idx, action);
            ShowGroupStatus(result.Message, result.Ok);
            if (result.Changed) AfterGroupingChange();
            return result;
        }

        /// <summary>Redraw the grouping after the editor changed (action, undo, redo, regroup).</summary>
        private void AfterGroupingChange()
        {
            ComputeGroupTexts();
            RefreshAllRows();
            UpdateGroupingButtons();
            UpdateStats();
            _changed = true;

            // Keep the detail line in sync with the new snippet
            int pos = SelectedPosition();
            if (pos >= 0 && _cur != null)
                _lblTime.SetText(DescribeLine(_items[pos].Index, _cur));
        }

        private void ShowGroupStatus(string message, bool ok)
        {
            _lblGroupStatus.SetText(message ?? "");
            if (ok) _lblGroupStatus.RemoveCssClass("error");
            else _lblGroupStatus.AddCssClass("error");
        }

        private void UpdateGroupingButtons()
        {
            _btnUndo?.SetSensitive(_editor != null && _editor.CanUndo);
            _btnRedo?.SetSensitive(_editor != null && _editor.CanRedo);
            int ep = _comboEp != null ? (int)_comboEp.GetSelected() : -1;
            _btnNextDiff?.SetSensitive(_editor != null && _wv?.ProposedJoins != null
                && ep >= 0 && ep < _wv.ProposedJoins.Count);
        }

        internal void SelectRow(int pos)
        {
            if (pos < 0 || pos >= _store.GetNItems()) return;
            var all = Gtk.Bitset.NewRange(0, _store.GetNItems());
            var one = Gtk.Bitset.NewEmpty();
            one.Add((uint)pos);
            _selection.SetSelection(one, all);
            try { _columnView.ScrollTo((uint)pos, null, Gtk.ListScrollFlags.Focus, null); } catch { }
        }

        private void OnRegroupRules(Gtk.Button s, EventArgs e)
        {
            if (_editor == null || _wv == null) return;
            int ep = (int)_comboEp.GetSelected();
            if (ep < 0 || ep >= _wv.CombinedAll.Count) return;

            var limits = SnippetLimits.FromSettings();
            var options = RuleGrouperOptions.FromSettings();
            _editor.Limits = limits;
            foreach (var line in _wv.CombinedAll[ep]) line.GroupNote = null;
            bool[] joins = ProposeJoins(_wv.CombinedAll[ep], SnippetMode.Rules, limits, options);
            _editor.SetJoins(joins);

            _wv.ProposedJoins ??= new List<bool[]>();
            while (_wv.ProposedJoins.Count < _wv.CombinedAll.Count) _wv.ProposedJoins.Add(null);
            _wv.ProposedJoins[ep] = (bool[])joins.Clone();
            _wv.ProposalProducer = "rules";

            ShowGroupStatus("Regrouped with the rules.", true);
            AfterGroupingChange();
        }

        private void OnUngroupAll(Gtk.Button s, EventArgs e)
        {
            if (_editor == null) return;
            _editor.UngroupAll();
            foreach (var line in _editor.Lines) line.GroupNote = null;
            ShowGroupStatus("All lines are single cards again.", true);
            AfterGroupingChange();
        }

        private void OnNextDisagreement(Gtk.Button s, EventArgs e)
        {
            if (_editor == null || _wv?.ProposedJoins == null) return;
            int ep = (int)_comboEp.GetSelected();
            if (ep < 0 || ep >= _wv.ProposedJoins.Count || _wv.ProposedJoins[ep] == null) return;

            int pos = SelectedPosition();
            int from = pos >= 0 ? _items[pos].Index : -1;
            int next = _editor.NextDisagreement(_wv.ProposedJoins[ep], from);
            if (next < 0)
            {
                ShowGroupStatus("No more differences from the proposal.", true);
                return;
            }
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].Index == next) { SelectRow(i); break; }
            ShowGroupStatus($"Line {next + 1}: your grouping differs from the proposal here.", true);
        }

        // ── SNAPSHOT PREVIEW ────────────────────────────────────────────────

        private void UpdateSnapshot(InfoCombined comb)
        {
            if (!_chkSnap.GetActive()
                || Settings.Instance.VideoClips.Files == null
                || Settings.Instance.VideoClips.Files.Length == 0) return;

            _currentSnapshotPath = null;

            int ep = (int)_comboEp.GetSelected();
            if (ep < 0 || ep >= Settings.Instance.VideoClips.Files.Length) return;

            string video = Settings.Instance.VideoClips.Files[ep];
            TimeSpan mid = UtilsSubs.getSnapshotTime(comb);
            string outFile = SysPath.Combine(_wv.MediaDir,
                ConstantSettings.TempImageFilename);

            try { File.Delete(outFile); } catch { }

            Task.Run(() =>
            {
                UtilsSnapshot.takeSnapshotFromVideo(video, mid,
                    Settings.Instance.Snapshots.Size,
                    Settings.Instance.Snapshots.Crop,
                    Settings.Instance.Snapshots.Quality,
                    outFile);
            }).ContinueWith(_ =>
            {
                if (_destroyed) return;
                GLib.Functions.IdleAdd(0, () =>
                {
                    if (_destroyed) return false;
                    try
                    {
                        if (File.Exists(outFile))
                        {
                            // GTK4: load texture from file for Gtk.Picture
                            var gfile = Gio.FileHelper.NewForPath(outFile);
                            _imgSnap.SetFile(gfile);
                            _currentSnapshotPath = outFile;
                        }
                    }
                    catch { }
                    return false;
                });
            });
        }

        /// <summary>
        /// Open the current snapshot in the default system image viewer.
        /// </summary>
        private void OnSnapshotClicked(Gtk.GestureClick sender,
            Gtk.GestureClick.ReleasedSignalArgs args)
        {
            if (string.IsNullOrEmpty(_currentSnapshotPath)
                || !File.Exists(_currentSnapshotPath))
                return;

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // ShellExecute opens the file with its registered viewer.
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _currentSnapshotPath,
                        UseShellExecute = true
                    });
                    return;
                }

                string opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
                Process.Start(new ProcessStartInfo
                {
                    FileName = opener,
                    Arguments = $"\"{_currentSnapshotPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch { }
        }

        // ── TEXT EDITING ────────────────────────────────────────────────────

        private void OnS1Changed(Gtk.Editable sender, EventArgs e)
        {
            if (_guard || _cur == null || _wv == null) return;
            _cur.Subs1.Text = _txtS1.GetText();
            _changed = true;
        }

        private void OnS2Changed(Gtk.Editable sender, EventArgs e)
        {
            if (_guard || _cur == null || _wv == null) return;
            _cur.Subs2.Text = _txtS2.GetText();
            _changed = true;
        }

        // ── ACTIVATE / DEACTIVATE ───────────────────────────────────────────

        private void OnActivate(Gtk.Button s, EventArgs e) => SetActiveSel(true);
        private void OnDeactivate(Gtk.Button s, EventArgs e) => SetActiveSel(false);

        private void SetActiveSel(bool active)
        {
            if (_wv == null) return;
            int ep = (int)_comboEp.GetSelected();
            if (ep < 0 || ep >= _wv.CombinedAll.Count) return;

            var bitset = _selection.GetSelection();
            if (bitset == null || bitset.GetSize() == 0) return;

            var arr = _wv.CombinedAll[ep];

            // Collect all selected positions
            var positions = new List<uint>();
            // Iterate the bitset: first, then walk with GetNth or iterate range
            uint size = (uint)bitset.GetSize();
            for (uint n = 0; n < size; n++)
            {
                uint pos = bitset.GetNth(n);
                if (pos < _items.Count)
                    positions.Add(pos);
            }

            // Apply active state to all selected items
            foreach (uint pos in positions)
            {
                var item = _items[(int)pos];
                int idx = item.Index;
                if (idx >= 0 && idx < arr.Count)
                {
                    arr[idx].Active = active;
                    item.IsActive = active;
                }
            }

            // The kept set changed, so neighbours and bands change too
            _editor?.RefreshKept();
            ComputeGroupTexts();

            // Refresh rows to update CSS classes, preserve selection
            _guard = true;
            var savedBitset = Gtk.Bitset.NewEmpty();
            foreach (uint pos in positions)
                savedBitset.Add(pos);

            uint count = _store.GetNItems();
            _store.RemoveAll();
            for (uint i = 0; i < count; i++)
                _store.Append(Gtk.StringObject.New(""));

            // Restore multi-selection
            _selection.SetSelection(savedBitset, savedBitset);
            _guard = false;

            UpdateStats();
            _changed = true;
        }

        // ── SELECT ALL / NONE / INVERT ─────────────────────────────────────

        private void OnSelectAll(Gtk.Button s, EventArgs e) => SetActiveAll(true);
        private void OnSelectNone(Gtk.Button s, EventArgs e) => SetActiveAll(false);

        private void OnInvertSelection(Gtk.Button s, EventArgs e)
        {
            if (_wv == null) return;
            int ep = (int)_comboEp.GetSelected();
            if (ep < 0 || ep >= _wv.CombinedAll.Count) return;

            var arr = _wv.CombinedAll[ep];
            for (int i = 0; i < _items.Count; i++)
            {
                int idx = _items[i].Index;
                if (idx >= 0 && idx < arr.Count)
                {
                    bool flipped = !arr[idx].Active;
                    arr[idx].Active = flipped;
                    _items[i].IsActive = flipped;
                }
            }

            _editor?.RefreshKept();
            ComputeGroupTexts();
            RefreshAllRows();
            UpdateStats();
            _changed = true;
        }

        private void SetActiveAll(bool active)
        {
            if (_wv == null) return;
            int ep = (int)_comboEp.GetSelected();
            if (ep < 0 || ep >= _wv.CombinedAll.Count) return;

            var arr = _wv.CombinedAll[ep];
            for (int i = 0; i < _items.Count; i++)
            {
                int idx = _items[i].Index;
                if (idx >= 0 && idx < arr.Count)
                {
                    arr[idx].Active = active;
                    _items[i].IsActive = active;
                }
            }

            _editor?.RefreshKept();
            ComputeGroupTexts();
            RefreshAllRows();
            UpdateStats();
            _changed = true;
        }

        /// <summary>
        /// Rebuild the dummy ListStore so that all rows re-bind with updated CSS classes.
        /// Preserves the current multi-selection.
        /// </summary>
        private void RefreshAllRows()
        {
            _guard = true;
            uint count = _store.GetNItems();

            // Save current selection bitset
            var oldSel = _selection.GetSelection();
            var saveBitset = Gtk.Bitset.NewEmpty();
            if (oldSel != null)
            {
                uint sz = (uint)oldSel.GetSize();
                for (uint n = 0; n < sz; n++)
                    saveBitset.Add(oldSel.GetNth(n));
            }

            _store.RemoveAll();
            for (uint i = 0; i < count; i++)
                _store.Append(Gtk.StringObject.New(""));

            // Restore selection
            if (saveBitset.GetSize() > 0)
                _selection.SetSelection(saveBitset, saveBitset);

            _guard = false;
        }

        // ── FIND ────────────────────────────────────────────────────────────

        /// <summary>
        /// Find next matching line starting from the currently selected row.
        /// If nothing is selected, search starts from the beginning.
        /// Wraps around to the start when reaching the end of the list.
        /// </summary>
        private void FindNext()
        {
            string text = _txtFind.GetText().Trim().ToLower();
            if (text.Length == 0 || _wv == null) return;

            int ep = (int)_comboEp.GetSelected();
            if (ep < 0) return;
            var arr = _wv.CombinedAll[ep];
            int count = arr.Count;
            if (count == 0) return;

            // Start searching from the row after the current selection
            int startFrom = 0;
            var bitset = _selection.GetSelection();
            if (bitset != null && bitset.GetSize() > 0)
                startFrom = (int)bitset.GetMaximum();

            for (int offset = 1; offset <= count; offset++)
            {
                int i = (startFrom + offset) % count;
                var cb = arr[i];
                if (cb.Subs1.Text.ToLower().Contains(text) ||
                    cb.Subs2.Text.ToLower().Contains(text))
                {
                    // Clear previous selection, select only the found item
                    var all = Gtk.Bitset.NewRange(0, _store.GetNItems());
                    var one = Gtk.Bitset.NewEmpty();
                    one.Add((uint)i);
                    _selection.SetSelection(one, all);
                    // ColumnView auto-scrolls to the focused/selected item
                    return;
                }
            }
        }

        // ── AUDIO PREVIEW ───────────────────────────────────────────────────

        /// <summary>
        /// The media ranges the selected line's card would be cut from: the shared
        /// range list of the snippet when the line is grouped (kept lines only,
        /// omitted lines cut away, gaps shortened when gap removal is on),
        /// otherwise the line itself. Pads applied at the edges.
        /// </summary>
        private List<TimeRange> RangesForAudioPreview(int idx, InfoCombined comb)
        {
            int padStart = Settings.Instance.AudioClips.PadEnabled ? Settings.Instance.AudioClips.PadStart : 0;
            int padEnd = Settings.Instance.AudioClips.PadEnabled ? Settings.Instance.AudioClips.PadEnd : 0;
            CardCutOptions cut = CardCutOptions.FromSettings();

            var range = _editor?.SnippetRangeOf(idx);
            if (range != null && range.Value.first != range.Value.last)
            {
                var ranges = SnippetMedia.RangesFor(_editor.Lines, range.Value.first, range.Value.last, cut);
                if (ranges.Count > 0) return ranges;
            }

            var plain = SnippetMedia.RangesFor(comb, cut, padStart, padEnd);
            if (plain != null && plain.Count > 0) return plain;

            return new List<TimeRange>
            {
                new TimeRange(UtilsSubs.applyTimePad(comb.Subs1.StartTime, -padStart),
                              UtilsSubs.applyTimePad(comb.Subs1.EndTime, padEnd))
            };
        }

        private async void OnPreviewAudio(Gtk.Button s, EventArgs e)
        {
            if (_cur == null || _wv == null) return;
            int ep = (int)_comboEp.GetSelected();
            if (ep < 0) return;

            _btnAudio.SetSensitive(false);
            _btnAudio.SetLabel("Extracting...");

            var comb = _cur;
            int pos = SelectedPosition();
            int idx = pos >= 0 ? _items[pos].Index : -1;
            List<TimeRange> ranges = RangesForAudioPreview(idx, comb);

            string mp3 = SysPath.Combine(_wv.MediaDir,
                ConstantSettings.TempAudioFilename);
            string wav = SysPath.Combine(_wv.MediaDir,
                ConstantSettings.TempAudioPreviewFilename);

            string errorMsg = null;

            await Task.Run(() =>
            {
                try { if (File.Exists(mp3)) File.Delete(mp3); } catch { }
                try { if (File.Exists(wav)) File.Delete(wav); } catch { }

                TimeSpan st = ranges[0].Start, en = ranges[ranges.Count - 1].End;

                if (Settings.Instance.AudioClips.UseAudioFromVideo &&
                    Settings.Instance.VideoClips.Files?.Length > ep)
                {
                    string streamNum =
                        Settings.Instance.VideoClips.AudioStream?.Num;
                    if (string.IsNullOrEmpty(streamNum)
                        || streamNum == "-" || !streamNum.Contains(":"))
                        streamNum = "0:a:0";

                    try
                    {
                        var audioFormat = Settings.Instance.AudioClips.AudioFormat;
                        var audioCodec = audioFormat == "Opus"
                            ? UtilsVideo.AudioCodec.Opus
                            : UtilsVideo.AudioCodec.MP3;

                        if (ranges.Count > 1)
                            UtilsAudio.ripAudioFromVideoSegments(
                                Settings.Instance.VideoClips.Files[ep],
                                streamNum, ranges,
                                Settings.Instance.AudioClips.Bitrate, mp3, audioCodec);
                        else
                            UtilsAudio.ripAudioFromVideo(
                                Settings.Instance.VideoClips.Files[ep],
                                streamNum, st, en,
                                Settings.Instance.AudioClips.Bitrate, mp3, null,
                                audioCodec);

                        if (!File.Exists(mp3) || new FileInfo(mp3).Length == 0)
                            errorMsg = "Failed to extract audio: output file not created or empty.";
                    }
                    catch (Exception ex)
                    {
                        errorMsg = "Failed to extract audio from video: " + ex.Message;
                    }
                }
                else if (Settings.Instance.AudioClips.UseExistingAudio &&
                         Settings.Instance.AudioClips.Files?.Length > ep)
                {
                    try
                    {
                        string existingAudio = Settings.Instance.AudioClips.Files[ep];
                        if (ranges.Count > 1)
                            UtilsAudio.cutAndEncodeAudioSegments(existingAudio, ranges,
                                Settings.Instance.AudioClips.Bitrate, mp3);
                        else
                            UtilsAudio.cutAudio(existingAudio, st, en, mp3);

                        if (!File.Exists(mp3) || new FileInfo(mp3).Length == 0)
                            errorMsg = "Failed to cut audio: output file not created or empty.";
                    }
                    catch (Exception ex)
                    {
                        errorMsg = "Failed to cut audio from existing file: " + ex.Message;
                    }
                }
                else
                {
                    errorMsg = "No audio source available. Check Video/Audio file settings in Preferences.";
                }

                if (string.IsNullOrEmpty(errorMsg) &&
                    File.Exists(mp3) && new FileInfo(mp3).Length > 0)
                {
                    try
                    {
                        UtilsAudio.convertAudioFormat(mp3, wav, 2);
                    }
                    catch (Exception ex)
                    {
                        errorMsg = "Failed to convert audio to WAV: " + ex.Message;
                    }
                }
            });

            if (_destroyed) return;

            _btnAudio.SetSensitive(true);
            _btnAudio.SetLabel("Preview Audio");

            if (!string.IsNullOrEmpty(errorMsg))
            {
                UtilsMsg.showErrMsg(errorMsg + "\n\nCheck terminal for full ffmpeg output.");
                return;
            }

            if (File.Exists(wav))
            {
                try
                {
                    var p = new Process();
                    p.StartInfo.FileName = ConstantSettings.ResolveToolOrName("ffplay");
                    p.StartInfo.Arguments =
                        $"-nodisp -autoexit -loglevel quiet \"{wav}\"";
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.CreateNoWindow = true;
                    p.Start();
                }
                catch (Exception ex)
                {
                    UtilsMsg.showErrMsg("Failed to play audio: " + ex.Message);
                }
            }
            else
            {
                UtilsMsg.showErrMsg("Audio preview file was not created. Check ffmpeg output.");
            }
        }

        // ── EPISODE CHANGE ──────────────────────────────────────────────────

        private void OnEpisodeChanged()
        {
            if (_guard || _wv == null) return;
            int ep = (int)_comboEp.GetSelected();
            if (ep < 0 || ep >= _wv.CombinedAll.Count) return;
            _guard = true;
            PopulateList(ep);
            _guard = false;
            UpdateStats();
        }

        // ── STATS ───────────────────────────────────────────────────────────

        private void UpdateStats()
        {
            if (_wv?.CombinedAll == null) return;
            int ep = (int)_comboEp.GetSelected();
            if (ep < 0 || ep >= _wv.CombinedAll.Count) return;

            int epL = 0, epA = 0, epI = 0, tL = 0, tA = 0, tI = 0;
            foreach (var cb in _wv.CombinedAll[ep])
            {
                epL++;
                if (cb.Active) epA++; else epI++;
            }
            foreach (var arr in _wv.CombinedAll)
                foreach (var cb in arr)
                {
                    tL++;
                    if (cb.Active) tA++; else tI++;
                }

            int cards = epA;
            if (_editor != null)
            {
                int[] kept = _editor.Kept;
                cards = SnippetGrouping.JoinsToRanges(SnippetGrouping.ProjectToKept(_editor.Joins, kept), kept.Length).Count;
            }

            _lblEpL.SetText(epL.ToString());
            _lblEpA.SetText(epA.ToString());
            _lblEpI.SetText(epI.ToString());
            _lblEpC.SetText(cards.ToString());
            _lblTL.SetText(tL.ToString());
            _lblTA.SetText(tA.ToString());
            _lblTI.SetText(tI.ToString());
        }

        // ── REGENERATE ──────────────────────────────────────────────────────

        private void OnRegenClicked(Gtk.Button s, EventArgs e)
        {
            if (_running) return;
            if (_changed)
            {
                if (!UtilsMsg.showConfirm("Regenerate and discard changes?"))
                    return;
            }
            _changed = false;
            RefreshSettings?.Invoke(this, EventArgs.Empty);

            _guard = true;
            _wv = null;
            _cur = null;
            _editor = null;
            _editorEp = -1;
            _store.RemoveAll();
            _items.Clear();
            _guard = false;

            LoadKeyBindings();
            PopulateEpCombo();
            RunPreviewAsync();
        }

        // ── VALIDATION EXPORT ───────────────────────────────────────────────

        /// <summary>The directory "Save as validation" writes to: the preference, or &lt;OutputDir&gt;/validation.</summary>
        internal static string ResolveValidationDir()
        {
            string dir = (ConstantSettings.ValidationDir ?? "").Trim();
            if (dir.Length > 0) return dir;
            return SysPath.Combine(Settings.Instance.OutputDir ?? "", "validation");
        }

        private void OnSaveValidation(Gtk.Button s, EventArgs e)
        {
            try
            {
                int n = SaveValidationFiles();
                UtilsMsg.showInfoMsg($"Saved {n} validation file{(n == 1 ? "" : "s")} to:\n{ResolveValidationDir()}");
            }
            catch (Exception ex)
            {
                UtilsMsg.showErrMsg("Could not save validation files: " + ex.Message);
            }
        }

        /// <summary>Write one grouping file per episode; returns how many were written.</summary>
        internal int SaveValidationFiles()
        {
            if (_wv?.CombinedAll == null || _wv.Joins == null) return 0;
            RefreshSettings?.Invoke(this, EventArgs.Empty);

            string dir = ResolveValidationDir();
            var limits = SnippetLimits.FromSettings();
            string deck = Settings.Instance.DeckName;
            if (string.IsNullOrEmpty(deck)) deck = "deck";
            int written = 0;

            for (int ep = 0; ep < _wv.CombinedAll.Count && ep < _wv.Joins.Count; ep++)
            {
                int episodeNumber = ep + Settings.Instance.EpisodeStartNumber;
                string subs1 = Settings.Instance.Subs[0].Files.Length > ep ? Settings.Instance.Subs[0].Files[ep] : "";
                string subs2 = Settings.Instance.Subs[1].Files.Length > ep ? Settings.Instance.Subs[1].Files[ep] : null;
                bool[] proposal = _wv.ProposedJoins != null && ep < _wv.ProposedJoins.Count ? _wv.ProposedJoins[ep] : null;

                var file = GroupingValidationFile.Build(_wv.CombinedAll[ep], _wv.Joins[ep], proposal,
                    _wv.ProposalProducer ?? "rules", limits, episodeNumber, subs1, subs2,
                    _wv.ProposalModel, _wv.ProposalPromptVersion);
                file.Write(SysPath.Combine(dir, GroupingValidationFile.FileName(deck, episodeNumber)));
                written++;
            }

            return written;
        }

        // ── GO (delegate to MainWindow) ─────────────────────────────────────

        private void OnGoClicked(Gtk.Button s, EventArgs e)
        {
            if (_wv?.CombinedAll == null) return;
            RefreshSettings?.Invoke(this, EventArgs.Empty);
            GoRequested?.Invoke(this, EventArgs.Empty);
        }

        // ── PROGRESS REPORTER ───────────────────────────────────────────────

        private class PProgress : IProgressReporter
        {
            private readonly Gtk.ProgressBar _b;
            private readonly Func<bool> _isDestroyed;
            private readonly CancellationTokenSource _cts = new();
            private bool _cancel;
            public int StepsTotal { get; set; }

            private string _text;
            private double _fraction = -1;
            private bool _dirty;
            private readonly object _sync = new object();
            private bool _active = true;

            public bool Cancel
            {
                get => _cancel;
                set { _cancel = value; if (value) _cts.Cancel(); }
            }

            public CancellationToken Token => _cts.Token;

            public PProgress(Gtk.ProgressBar b, Func<bool> isDestroyed)
            {
                _b = b;
                _isDestroyed = isDestroyed;
                // Poll for UI updates every 50ms
                GLib.Functions.TimeoutAdd(0, 50, OnPoll);
            }

            public void Stop()
            {
                _active = false;
                if (!_isDestroyed()) OnPoll();
            }

            private bool OnPoll()
            {
                if (_isDestroyed()) return false;
                string text;
                double frac;
                bool dirty;
                lock (_sync)
                {
                    text = _text;
                    frac = _fraction;
                    dirty = _dirty;
                    _dirty = false;
                }
                if (dirty)
                {
                    if (text != null) _b.SetText(text);
                    if (frac >= 0) _b.SetFraction(frac);
                }
                return _active;
            }

            public void NextStep(int step, string desc)
            {
                lock (_sync)
                {
                    _text = $"[{step}/{StepsTotal}] {desc}";
                    _fraction = 0.0;
                    _dirty = true;
                }
            }

            public void UpdateProgress(int pct, string text)
            {
                lock (_sync)
                {
                    _text = text;
                    _fraction = Math.Max(0, Math.Min(1, pct / 100.0));
                    _dirty = true;
                }
            }

            public void UpdateProgress(string text)
            {
                lock (_sync) { _text = text; _dirty = true; }
            }

            public void EnableDetail(bool en) { }
            public void SetDuration(TimeSpan d) { }
            public void OnFFmpegOutput(object sender, DataReceivedEventArgs e) { }
        }
    }

    // ── PreviewItem GObject wrapper ─────────────────────────────────────────
    // Gio.ListStore requires items that are GObject subclasses.
    // This lightweight wrapper holds the display data for one preview row.

    /// <summary>
    /// Plain data holder for a single preview list row.
    /// Cannot subclass GObject.Object in Gir.Core 0.7 (no public parameterless ctor).
    /// Data is stored in a parallel List; ListStore holds dummy StringObjects.
    /// </summary>
    public class PreviewItem
    {
        public string Subs1Text { get; set; } = "";
        public string Subs2Text { get; set; } = "";
        public string StartText { get; set; } = "";
        public string EndText { get; set; } = "";
        public string DurText { get; set; } = "";
        public bool IsActive { get; set; } = true;
        public int Index { get; set; } = -1;

        /// <summary>Bracket and trimmed duration drawn in the Group column ("" for a single line).</summary>
        public string GroupText { get; set; } = "";

        /// <summary>Gap to the previous kept line, in seconds ("" for the first kept line).</summary>
        public string GapText { get; set; } = "";

        /// <summary>The grouper's note for the snippet this line is in (tooltip of the Group column).</summary>
        public string GroupNote { get; set; } = "";

        /// <summary>0 = not in a multi-line snippet, 1/2 = alternating colour band.</summary>
        public int Band { get; set; }

        public static PreviewItem Create(string s1, string s2,
            string start, string end, string dur, bool active, int idx)
        {
            return new PreviewItem
            {
                Subs1Text = s1, Subs2Text = s2,
                StartText = start, EndText = end, DurText = dur,
                IsActive = active, Index = idx
            };
        }
    }
}
