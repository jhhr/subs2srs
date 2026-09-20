//  Copyright (C) 2026 jhhr and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later
//
//  This file is part of subs2srs.
//
//  subs2srs is free software: you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.

using System;
using System.IO;
using System.Threading;

namespace subs2srs
{
  /// <summary>
  /// Launcher for the external subsretimer tool. Prefilled with the main
  /// window's Subs1/Subs2 files, lets the user pick which one is the
  /// reference, runs the tool and hands the retimed file back.
  /// Modal Gtk.Window + nested GLib.MainLoop, like the other tool dialogs.
  /// </summary>
  public class DialogSubsRetimer : Gtk.Window
  {
    private readonly string _subs1EncodingShort;
    private readonly string _subs2EncodingShort;

    private Gtk.CheckButton _radioRefSubs1;
    private Gtk.CheckButton _radioRefSubs2;
    private Gtk.Entry _txtSubs1;
    private Gtk.Entry _txtSubs2;
    private Gtk.CheckButton _chkAuto;
    private Gtk.Label _lblStatus;
    private Gtk.Button _btnRun;
    private Gtk.Button _btnCancel;
    private Gtk.Spinner _spinner;

    private CancellationTokenSource _cts;
    private bool _running;
    private GLib.MainLoop _loop;
    private int _result;

    /// <summary>Path of the retimed file when the user chose to use it, else null.</summary>
    public string SavedPath { get; private set; }

    /// <summary>1 or 2: which main-window field <see cref="SavedPath"/> should replace.</summary>
    public int RetimedSide { get; private set; }

    public DialogSubsRetimer(Gtk.Window parent, string subs1Path, string subs1EncodingLong,
                             string subs2Path, string subs2EncodingLong) : base()
    {
      _subs1EncodingShort = InfoEncoding.longToShort(subs1EncodingLong);
      _subs2EncodingShort = InfoEncoding.longToShort(subs2EncodingLong);

      SetTitle("Subs Re-Timer");
      SetDefaultSize(560, 320);
      SetModal(true);
      if (parent != null) SetTransientFor(parent);
      OnCloseRequest += OnDialogCloseRequest;

      BuildUI();
      _txtSubs1.SetText(subs1Path ?? "");
      _txtSubs2.SetText(subs2Path ?? "");
      if (ConstantSettings.SubsRetimerReferenceIsSubs2) _radioRefSubs2.SetActive(true);
      else _radioRefSubs1.SetActive(true);
      _chkAuto.SetActive(ConstantSettings.SubsRetimerAuto);
      UpdateStatus();
    }

    public int Run()
    {
      _loop = GLib.MainLoop.New(null, false);
      Show();
      _loop.Run();
      return _result;
    }

    private bool OnDialogCloseRequest(Gtk.Window sender, EventArgs args)
    {
      _cts?.Cancel();
      if (_loop != null && _loop.IsRunning()) _loop.Quit();
      return false;
    }

    private void BuildUI()
    {
      var vbox = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
      vbox.SetMarginTop(10); vbox.SetMarginBottom(10);
      vbox.SetMarginStart(10); vbox.SetMarginEnd(10);

      var help = Gtk.Label.New(
        "Re-time one subtitle file so its lines follow the timings of the other.\n" +
        "The reference is the file that already matches your video.");
      help.SetHalign(Gtk.Align.Start);
      help.SetWrap(true);
      vbox.Append(help);
      vbox.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));

      var grid = Gtk.Grid.New();
      grid.SetColumnSpacing(8);
      grid.SetRowSpacing(6);

      var lblRef = Gtk.Label.New("Reference (already timed):");
      lblRef.SetHalign(Gtk.Align.Start);
      grid.Attach(lblRef, 0, 0, 1, 1);
      var radioBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
      _radioRefSubs1 = Gtk.CheckButton.NewWithLabel("Subs1");
      _radioRefSubs2 = Gtk.CheckButton.NewWithLabel("Subs2");
      _radioRefSubs2.SetGroup(_radioRefSubs1);
      _radioRefSubs1.OnToggled += (s, e) => UpdateStatus();
      radioBox.Append(_radioRefSubs1);
      radioBox.Append(_radioRefSubs2);
      grid.Attach(radioBox, 1, 0, 2, 1);

      _txtSubs1 = AddFileRow(grid, 1, "Subs1:");
      _txtSubs2 = AddFileRow(grid, 2, "Subs2:");
      vbox.Append(grid);

      _chkAuto = Gtk.CheckButton.NewWithLabel("Auto-align without opening the editor");
      vbox.Append(_chkAuto);

      _lblStatus = Gtk.Label.New("");
      _lblStatus.SetHalign(Gtk.Align.Start);
      _lblStatus.SetWrap(true);
      _lblStatus.SetVexpand(true);
      _lblStatus.SetValign(Gtk.Align.Start);
      vbox.Append(_lblStatus);

      var buttons = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
      buttons.SetHalign(Gtk.Align.End);
      _spinner = Gtk.Spinner.New();
      buttons.Append(_spinner);
      _btnCancel = Gtk.Button.NewWithLabel("Close");
      _btnCancel.OnClicked += (s, e) => { _cts?.Cancel(); Close(); };
      buttons.Append(_btnCancel);
      _btnRun = Gtk.Button.NewWithLabel("Run Subs Re-Timer");
      _btnRun.AddCssClass("suggested-action");
      _btnRun.OnClicked += OnRunClicked;
      buttons.Append(_btnRun);
      vbox.Append(buttons);

      SetChild(vbox);
    }

    private Gtk.Entry AddFileRow(Gtk.Grid grid, int row, string caption)
    {
      var lbl = Gtk.Label.New(caption);
      lbl.SetHalign(Gtk.Align.Start);
      grid.Attach(lbl, 0, row, 1, 1);
      var entry = Gtk.Entry.New();
      entry.SetHexpand(true);
      entry.OnChanged += (s, e) => UpdateStatus();
      grid.Attach(entry, 1, row, 1, 1);
      var btn = Gtk.Button.NewWithLabel("Browse...");
      btn.OnClicked += (s, e) => BrowseSubFile(entry);
      grid.Attach(btn, 2, row, 1, 1);
      return entry;
    }

    private async void BrowseSubFile(Gtk.Entry target)
    {
      var dlg = Gtk.FileDialog.New();
      dlg.SetTitle("Select Subtitle File");
      var filter = Gtk.FileFilter.New();
      filter.SetName("Subtitle Files (*.ass;*.ssa;*.srt)");
      filter.AddPattern("*.ass"); filter.AddPattern("*.ssa"); filter.AddPattern("*.srt");
      var filters = Gio.ListStore.New(Gtk.FileFilter.GetGType());
      filters.Append(filter);
      dlg.SetFilters(filters);
      try
      {
        var file = await dlg.OpenAsync(this);
        if (file != null) target.SetText(file.GetPath() ?? "");
      }
      catch { /* user cancelled */ }
    }

    private bool ReferenceIsSubs2 => _radioRefSubs2.GetActive();

    private (string Reference, string Target) Paths() =>
      ReferenceIsSubs2
        ? (_txtSubs2.GetText().Trim(), _txtSubs1.GetText().Trim())
        : (_txtSubs1.GetText().Trim(), _txtSubs2.GetText().Trim());

    private static bool IsSingleSubFile(string path)
    {
      if (path == "" || path.Contains('*') || path.Contains('?')) return false;
      string ext = Path.GetExtension(path).ToLowerInvariant();
      return (ext == ".ass" || ext == ".ssa" || ext == ".srt") && File.Exists(path);
    }

    private void UpdateStatus()
    {
      if (_running) return;
      var (reference, target) = Paths();
      string problem = null;
      if (!IsSingleSubFile(reference)) problem = "The reference must be an existing .ass/.ssa/.srt file (no wildcards).";
      else if (!IsSingleSubFile(target)) problem = "The file to re-time must be an existing .ass/.ssa/.srt file (no wildcards).";
      else if (string.Equals(Path.GetFullPath(reference), Path.GetFullPath(target), StringComparison.Ordinal))
        problem = "Reference and target are the same file.";

      _btnRun.SetSensitive(problem == null);
      _lblStatus.SetText(problem ?? $"Will re-time {Path.GetFileName(target)} to match {Path.GetFileName(reference)}.");
    }

    private async void OnRunClicked(Gtk.Button sender, EventArgs e)
    {
      var (reference, target) = Paths();
      bool refIsSubs2 = ReferenceIsSubs2;
      int retimedSide = refIsSubs2 ? 1 : 2;

      ConstantSettings.SubsRetimerReferenceIsSubs2 = refIsSubs2;
      ConstantSettings.SubsRetimerAuto = _chkAuto.GetActive();

      var request = new SubsRetimerLauncher.Request(
        reference, target,
        refIsSubs2 ? _subs2EncodingShort : _subs1EncodingShort,
        refIsSubs2 ? _subs1EncodingShort : _subs2EncodingShort,
        _chkAuto.GetActive());

      _running = true;
      _cts = new CancellationTokenSource();
      _btnRun.SetSensitive(false);
      _spinner.Start();
      _lblStatus.SetText(_chkAuto.GetActive() ? "Aligning..." : "Subs Re-Timer is open. Save there to continue.");

      var result = await SubsRetimerLauncher.RunAsync(request, _cts.Token);

      _spinner.Stop();
      _running = false;
      _cts = null;

      if (result.Saved)
      {
        _lblStatus.SetText("Saved " + Path.GetFileName(result.SavedPath));
        if (UtilsMsg.showConfirm(
              $"Re-timed file saved as:\n{result.SavedPath}\n\nUse it as Subs{retimedSide} in the main window?"))
        {
          SavedPath = result.SavedPath;
          RetimedSide = retimedSide;
          _result = 1;
          Close();
          return;
        }
      }
      else if (result.NothingSaved)
      {
        _lblStatus.SetText("Nothing was saved.");
      }
      else
      {
        _lblStatus.SetText("Subs Re-Timer failed.");
        UtilsMsg.showErrMsg("Subs Re-Timer failed:\n" + (result.StdErr == "" ? $"exit code {result.ExitCode}" : result.StdErr));
      }
      UpdateStatus();
    }
  }
}
