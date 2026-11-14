using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DockIsland;

// Uma nota rápida
public class NoteItem
{
    public string Text { get; set; }
    public string Time { get; set; }   // ex.: "13/11 21:40"
}

// =========================================================
//  Aba "Notas": anotações rápidas dentro da ilha.
//  Enter guarda a nota, Shift+Enter quebra a linha. O que estiver sendo digitado (rascunho)
//  e todas as notas ficam salvos em %AppData%\DockIsland\notes.json, então continuam ali
//  depois de fechar e abrir o programa.
//  Como na busca, a janela da ilha normalmente não ativa; só enquanto a aba Notas está aberta
//  ela passa a aceitar digitação, e volta ao normal ao sair.
// =========================================================
public partial class MainWindow
{
    class NotesFile
    {
        public string Draft { get; set; }
        public List<NoteItem> Notes { get; set; } = new();
    }

    static string NotesFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockIsland", "notes.json");

    const int MaxNotes = 200;

    readonly ObservableCollection<NoteItem> _notes = new();
    readonly DispatcherTimer _notesSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    bool _notesDirty, _notesLoading, _notesMode;

    void InitNotes()
    {
        NoteList.ItemsSource = _notes;
        LoadNotes();

        _notesSaveTimer.Tick += (_, __) => { _notesSaveTimer.Stop(); SaveNotes(); };
        _notes.CollectionChanged += (_, __) =>
        {
            NoNotesText.Visibility = _notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            QueueNotesSave();
        };

        // clicou fora (outra janela ficou em foco): fecha a aba (o rascunho continua guardado)
        Deactivated += (_, __) =>
        {
            if (_page != 7) return;
            SelectPage(0);
            if (!Island.IsMouseOver && !_menuOpen) Collapse();
        };
    }

    void QueueNotesSave()
    {
        if (_notesLoading) return;
        _notesDirty = true;
        _notesSaveTimer.Stop();
        _notesSaveTimer.Start();
    }

    // Grava na hora o que ainda estiver pendente (usado ao fechar o programa)
    void FlushNotes()
    {
        _notesSaveTimer.Stop();
        if (_notesDirty) SaveNotes();
    }

    void LoadNotes()
    {
        _notesLoading = true;
        try
        {
            if (File.Exists(NotesFilePath))
            {
                var data = JsonSerializer.Deserialize<NotesFile>(File.ReadAllText(NotesFilePath));
                if (data != null)
                {
                    foreach (var n in (data.Notes ?? new()).Take(MaxNotes))
                        if (!string.IsNullOrWhiteSpace(n?.Text)) _notes.Add(n);
                    NoteBox.Text = data.Draft ?? "";
                }
            }
        }
        catch { }
        _notesLoading = false;
        NoNotesText.Visibility = _notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoteHint.Visibility = NoteBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    void SaveNotes()
    {
        _notesDirty = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NotesFilePath));
            var data = new NotesFile { Draft = NoteBox.Text, Notes = _notes.ToList() };
            var tmp = NotesFilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data));
            File.Move(tmp, NotesFilePath, true);
        }
        catch { }
    }

    // ---------- Entrar / sair do modo de digitação ----------
    void EnterNotes()
    {
        if (_notesMode) return;
        _notesMode = true;
        _leaveTimer.Stop();
        _peekTimer.Stop();

        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero)
        {
            var fg = GetForegroundWindow();
            _prevFg = fg != h ? fg : IntPtr.Zero;
            // a ilha passa a poder receber o teclado
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) & ~WS_EX_NOACTIVATE);
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_notesMode) return;
            try
            {
                Activate();
                if (h != IntPtr.Zero) SetForegroundWindow(h);
                NoteBox.Focus();
                Keyboard.Focus(NoteBox);
                NoteBox.CaretIndex = NoteBox.Text.Length;
            }
            catch { }
        }), DispatcherPriority.Input);
    }

    void LeaveNotes()
    {
        if (!_notesMode) return;
        _notesMode = false;
        FlushNotes();

        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero)
        {
            bool weAreForeground = GetForegroundWindow() == h;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
            // devolve o foco para o programa em que você estava
            if (weAreForeground && _prevFg != IntPtr.Zero && IsWindowHandle(_prevFg))
            {
                try { SetForegroundWindow(_prevFg); } catch { }
            }
        }
        _prevFg = IntPtr.Zero;
    }

    // ---------- Campo de digitação ----------
    void NoteBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        NoteHint.Visibility = NoteBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        QueueNotesSave();   // guarda o rascunho
    }

    void NoteBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // Shift+Enter: quebra de linha normal; Enter sozinho: guarda a nota
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
            AddNote();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SelectPage(0);
            if (!Island.IsMouseOver) Collapse();
            e.Handled = true;
        }
    }

    void NoteAdd_Click(object sender, RoutedEventArgs e)
    {
        AddNote();
        NoteBox.Focus();
        Keyboard.Focus(NoteBox);
    }

    void AddNote()
    {
        var text = NoteBox.Text.Trim();
        if (text.Length == 0) return;

        _notes.Insert(0, new NoteItem { Text = text, Time = DateTime.Now.ToString("dd/MM HH:mm") });
        while (_notes.Count > MaxNotes) _notes.RemoveAt(_notes.Count - 1);
        NoteBox.Text = "";
        SaveNotes();   // grava na hora
        NoteList.ScrollIntoView(_notes[0]);
    }

    // ---------- Botões de cada nota ----------
    static NoteItem NoteOf(object sender) => (sender as FrameworkElement)?.DataContext as NoteItem;

    void NoteCopy_Click(object sender, RoutedEventArgs e)
    {
        var n = NoteOf(sender);
        if (n == null) return;
        try { Clipboard.SetText(n.Text); } catch { }
    }

    // Edita: devolve o texto para o campo e tira a nota da lista (Enter guarda de novo)
    void NoteEdit_Click(object sender, RoutedEventArgs e)
    {
        var n = NoteOf(sender);
        if (n == null) return;

        // se já havia um rascunho, ele vira uma nota para não ser perdido
        var draft = NoteBox.Text.Trim();
        int idx = _notes.IndexOf(n);
        if (idx >= 0) _notes.RemoveAt(idx);
        if (draft.Length > 0)
            _notes.Insert(0, new NoteItem { Text = draft, Time = DateTime.Now.ToString("dd/MM HH:mm") });

        NoteBox.Text = n.Text;
        SaveNotes();
        NoteBox.Focus();
        Keyboard.Focus(NoteBox);
        NoteBox.CaretIndex = NoteBox.Text.Length;
    }

    void NoteDelete_Click(object sender, RoutedEventArgs e)
    {
        var n = NoteOf(sender);
        if (n == null) return;
        _notes.Remove(n);
        SaveNotes();
    }
}
