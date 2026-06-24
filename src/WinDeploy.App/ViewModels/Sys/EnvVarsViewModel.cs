using System.Collections;
using System.Collections.ObjectModel;
using System.Security;
using WinDeploy.App.Services;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.ViewModels.Sys;

public sealed class EnvVarRow : ObservableObject
{
    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    private string _value = "";
    public string Value { get => _value; set => Set(ref _value, value); }
}

/// <summary>View / edit user or machine (system) environment variables.</summary>
public sealed class EnvVarsViewModel : ObservableObject
{
    public ObservableCollection<EnvVarRow> Rows { get; } = new();
    public RelayCommand ReloadCommand { get; }
    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand SaveCommand { get; }

    private HashSet<string> _originalNames = new(StringComparer.OrdinalIgnoreCase);

    public EnvVarsViewModel()
    {
        ReloadCommand = new RelayCommand(_ => Reload());
        AddCommand = new RelayCommand(_ => Rows.Add(new EnvVarRow { Name = "NEW_VAR" }));
        RemoveCommand = new RelayCommand(_ => { if (SelectedRow != null) Rows.Remove(SelectedRow); });
        SaveCommand = new RelayCommand(_ => Save());
        Reload();
    }

    private bool _machine;
    public bool IsMachine
    {
        get => _machine;
        set { if (Set(ref _machine, value)) { OnPropertyChanged(nameof(IsUser)); Reload(); } }
    }
    public bool IsUser { get => !_machine; set { if (value) IsMachine = false; } }

    private EnvVarRow? _selectedRow;
    public EnvVarRow? SelectedRow { get => _selectedRow; set => Set(ref _selectedRow, value); }

    private string _note = "";
    public string Note { get => _note; set => Set(ref _note, value); }

    private EnvironmentVariableTarget Target => _machine ? EnvironmentVariableTarget.Machine : EnvironmentVariableTarget.User;

    private void Reload()
    {
        Rows.Clear();
        _originalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var entries = Environment.GetEnvironmentVariables(Target).Cast<DictionaryEntry>()
                .OrderBy(e => (string)e.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                var name = (string)e.Key;
                Rows.Add(new EnvVarRow { Name = name, Value = (string)(e.Value ?? "") });
                _originalNames.Add(name);
            }
            Note = _machine ? Localizer.T("env.note.machine") : Localizer.T("env.note.user");
        }
        catch (Exception ex) { Note = Localizer.Format("env.note.loadFail", ex.Message); }
    }

    private void Save()
    {
        var target = Target;
        var current = new HashSet<string>(
            Rows.Select(r => r.Name.Trim()).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var r in Rows)
            {
                var n = r.Name.Trim();
                if (n.Length > 0) Environment.SetEnvironmentVariable(n, r.Value, target);
            }
            foreach (var old in _originalNames)
                if (!current.Contains(old)) Environment.SetEnvironmentVariable(old, null, target);

            var removed = _originalNames.Count(o => !current.Contains(o));
            _originalNames = current;
            Note = Localizer.T("env.note.saved");
            AuditLog.Action($"保存环境变量（{(_machine ? "系统" : "用户")}）：共 {current.Count} 项，移除 {removed} 项");
        }
        catch (SecurityException) { Note = Localizer.T("env.note.removeAdmin"); }
        catch (Exception ex) { Note = Localizer.Format("env.note.saveFail", ex.Message); }
    }
}
