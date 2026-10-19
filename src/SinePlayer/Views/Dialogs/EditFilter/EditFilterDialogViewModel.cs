// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;

using SinePlayer.Common.Expression;
using SinePlayer.Common.Expression.Filter;
using SinePlayer.Common.Expression.Filter.Sql;
using SinePlayer.Common.Localization;
using SinePlayer.Core.Common.Lifecycle;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Database.SqlHelpers;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Data.Tables;
using SinePlayer.Helpers.Search;
using SinePlayer.ViewModels;

namespace SinePlayer.Views.Dialogs.EditFilter;

internal partial class EditFilterDialogViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public MutableLiveData<string> NameLiveData = new();
    public MutableLiveData<string> ExpressionLiveData = new();
    public MutableLiveData<string> ParseResultLiveData = new();
    public MutableLiveData<bool> SaveEnableLiveData = new();
    public MutableLiveData<bool> SaveAsNewEnableLiveData = new();
    public MutableLiveData<ExpressionTokenInfo> AppendToExpressionLiveData = new();

    private MediaFilterModel.ExternalModel? _filterModel;
    private MediaFilterModel.ExternalFilterModel? _filter;
    private string? _name;
    private string? _expression;
    private bool _isNameValid = false;
    private bool _isNameExists = false;
    private bool _isExpressionValid = false;

    private ObservableCollection<TagViewModel> _expressionButtons = [];
    public ObservableCollection<TagViewModel> ExpressionButtons
    {
        get => _expressionButtons;
        set
        {
            _expressionButtons = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpressionButtons)));
        }
    }

    private bool _includeHiddenItems = false;
    public bool IncludeHiddenItems
    {
        get => _includeHiddenItems;
        set
        {
            _includeHiddenItems = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IncludeHiddenItems)));
        }
    }

    private bool _saveViewSettings = false;
    public bool SaveViewSettings
    {
        get => _saveViewSettings;
        set
        {
            _saveViewSettings = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SaveViewSettings)));
        }
    }

    private bool _saveSortingAndGroupingSettings = true;
    public bool SaveSortingAndGroupingSettings
    {
        get => _saveSortingAndGroupingSettings;
        set
        {
            _saveSortingAndGroupingSettings = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SaveSortingAndGroupingSettings)));
        }
    }

    public void Initialize(MediaFilterModel.ExternalFilterModel filter)
    {
        CoroutineUtils.Run(() => InitializeAsync(filter));
    }

    public void UpdateName(string name)
    {
        name = name.Trim();
        if (name == _name)
        {
            return;
        }
        _name = name;

        _isNameValid = name.Length > 0;
        _isNameExists = FindFilter(name) != null;
        UpdateButtonStates();
    }

    public void UpdateExpression(string expression)
    {
        if (expression == _expression)
        {
            return;
        }

        _expression = expression;

        MediaFilterModel.ExternalFilterModel? filter = _filter;
        if (filter == null)
        {
            return;
        }

        filter.Expression = expression;
        UpdateSQLQuery();
    }

    public void SetIncludeHiddenItems(bool includeHiddenItems)
    {
        MediaFilterModel.ExternalFilterModel? filter = _filter;
        if (filter is null)
        {
            return;
        }

        filter.IncludeHiddenItems = includeHiddenItems;
    }

    public void SetSaveViewSettings(bool save)
    {
        MediaFilterModel.ExternalFilterModel? filter = _filter;
        if (filter is null)
        {
            return;
        }

        filter.SaveViewSettings = save;
    }

    public void SetSaveSortingAndGroupingSettings(bool save)
    {
        MediaFilterModel.ExternalFilterModel? filter = _filter;
        if (filter is null)
        {
            return;
        }

        filter.SaveSortingAndGroupingSettings = save;
    }

    public void Save()
    {
        MediaFilterModel.ExternalFilterModel? filter = _filter;
        if (filter is null)
        {
            return;
        }

        RemoveFilter(filter.Name);
        filter.Name = _name ?? "";
        filter.Modified = false;
        OverwriteFilter(filter);

        MediaFilterModel.ExternalModel? model = _filterModel;
        if (model is null)
        {
            return;
        }

        model.LastFilter = filter.Clone();
        MediaFilterModel.Instance.UpdateModel(model);
    }

    public void SaveAsNew()
    {
        MediaFilterModel.ExternalFilterModel? filter = _filter;
        if (filter is null)
        {
            return;
        }

        filter = filter.Clone();
        filter.Name = _name ?? "";
        filter.Modified = false;
        OverwriteFilter(filter);

        MediaFilterModel.ExternalModel? model = _filterModel;
        if (model is null)
        {
            return;
        }

        model.LastFilter = filter.Clone();
        MediaFilterModel.Instance.UpdateModel(model);
    }

    public void Delete()
    {
        MediaFilterModel.ExternalFilterModel? filter = _filter;
        if (filter is null)
        {
            return;
        }

        RemoveFilter(filter.Name);

        MediaFilterModel.ExternalModel? model = _filterModel;
        if (model is null)
        {
            return;
        }

        model.LastFilter = null;
        MediaFilterModel.Instance.UpdateModel(model);
    }

    private async Task InitializeAsync(MediaFilterModel.ExternalFilterModel filter)
    {
        {
            void OnClickButton(string text, int cursorPosition = -1)
            {
                var info = new ExpressionTokenInfo()
                {
                    Text = text,
                    CursorPosition = cursorPosition
                };

                AppendToExpressionLiveData.Emit(info);
            }

            ObservableCollection<TagViewModel> buttons = [];
            buttons.Add(new() { Tag = StringResourceProvider.Instance.ExpressionAnd, OnClicked = () => OnClickButton("and ") });
            buttons.Add(new() { Tag = StringResourceProvider.Instance.ExpressionOr, OnClicked = () => OnClickButton("or ") });
            buttons.Add(new() { Tag = StringResourceProvider.Instance.ExpressionNot, OnClicked = () => OnClickButton("not ") });
            buttons.Add(new() { Tag = "=", OnClicked = () => OnClickButton("= ") });
            buttons.Add(new() { Tag = ">", OnClicked = () => OnClickButton("> ") });
            buttons.Add(new() { Tag = "<", OnClicked = () => OnClickButton("< ") });
            buttons.Add(new() { Tag = ">=", OnClicked = () => OnClickButton(">= ") });
            buttons.Add(new() { Tag = "<=", OnClicked = () => OnClickButton("<= ") });
            buttons.Add(new() { Tag = StringResourceProvider.Instance.ExpressionIn, OnClicked = () => OnClickButton("in ()", -2) });
            buttons.Add(new() { Tag = StringResourceProvider.Instance.Title, OnClicked = () => OnClickButton($"%{MediaSQLProviderUtils.VAR_TITLE}") });
            buttons.Add(new() { Tag = StringResourceProvider.Instance.Rating, OnClicked = () => OnClickButton($"%{MediaSQLProviderUtils.VAR_RATING}") });

            foreach (CompletionStatusEnum status in CompletionStatusService.AllStatus)
            {
                buttons.Add(new()
                {
                    Tag = CompletionStatusService.EnumToString(status),
                    OnClicked = () => OnClickButton($"%{MediaSQLProviderUtils.VAR_COMPLETION_STATUS} = {(int)status}")
                });
            }

            buttons.Add(new() { Tag = StringResourceProvider.Instance.Progress, OnClicked = () => OnClickButton($"%{MediaSQLProviderUtils.VAR_PROGRESS}") });
            buttons.Add(new() { Tag = StringResourceProvider.Instance.Title1, OnClicked = () => OnClickButton($"%{MediaSQLProviderUtils.VAR_TITLE1}") });
            buttons.Add(new() { Tag = StringResourceProvider.Instance.Title2, OnClicked = () => OnClickButton($"%{MediaSQLProviderUtils.VAR_TITLE2}") });
            buttons.Add(new() { Tag = StringResourceProvider.Instance.Duration, OnClicked = () => OnClickButton($"%{MediaSQLProviderUtils.VAR_DURATION}") });
            buttons.Add(new() { Tag = StringResourceProvider.Instance.Tag, OnClicked = () => OnClickButton($"%{MediaSQLProviderUtils.VAR_TAG}") });
            buttons.Add(new() { Tag = StringResourceProvider.Instance.Hidden, OnClicked = () => OnClickButton($"%{MediaSQLProviderUtils.VAR_HIDDEN}") });

            List<string> tagCategories = await MediaModel.GetAllTagCategories();
            foreach (string category in tagCategories)
            {
                buttons.Add(new()
                {
                    Tag = $"{StringResourceProvider.Instance.Tag}.{category}",
                    OnClicked = () => OnClickButton($"%{MediaSQLProviderUtils.VAR_TAG}.\"{ExpressionUtils.EscapeString(category)}\""),
                });
            }

            ExpressionButtons = buttons;
        }

        MediaFilterModel.ExternalModel? filterModel = MediaFilterModel.Instance.GetModel();
        _filterModel = filterModel;
        _filter = filter;
        if (filter is not null)
        {
            UpdateName(filter.Name);
            NameLiveData.Emit(filter.Name);
            UpdateExpression(filter.Expression);
            ExpressionLiveData.Emit(filter.Expression);
            IncludeHiddenItems = filter.IncludeHiddenItems;
            SaveViewSettings = filter.SaveViewSettings;
            SaveSortingAndGroupingSettings = filter.SaveSortingAndGroupingSettings;
        }
    }

    private void UpdateSQLQuery()
    {
        MediaFilterModel.ExternalFilterModel? filter = _filter;
        if (filter is null)
        {
            return;
        }

        void OnInvalidExpression(string message)
        {
            _isExpressionValid = false;
            string hintMessage = StringResourceProvider.Instance.ExpressionInvalid.Replace("$reason", message);
            ParseResultLiveData.Emit(hintMessage);
            UpdateButtonStates();
        }

        ExpressionToken token;
        try
        {
            token = ExpressionParser.ParseFilter(filter.Expression);
        }
        catch (ExpressionException e)
        {
            OnInvalidExpression(e.Message);
            return;
        }

        ICondition condition;
        try
        {
            condition = SQLGenerator.CreateQuery(token, new MediaFilterSQLProvider());
        }
        catch (ExpressionException e)
        {
            OnInvalidExpression(e.Message);
            return;
        }

        var command = SelectCommand.Create(MediaTable.Instance);
        command.PutQueryInt64(MediaTable.ColumnId);
        command.AppendCondition(condition);

        _isExpressionValid = true;
        string hintMessage = StringResourceProvider.Instance.ExpressionValid.Replace("$query", command.ToString());
        ParseResultLiveData.Emit(hintMessage);
        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        bool isInputValid = _isNameValid && _isExpressionValid;
        SaveEnableLiveData.Emit(isInputValid);
        SaveAsNewEnableLiveData.Emit(isInputValid && !_isNameExists);
    }

    private MediaFilterModel.ExternalFilterModel? FindFilter(string name)
    {
        MediaFilterModel.ExternalModel? filterModel = _filterModel;
        if (filterModel is null)
        {
            return null;
        }

        return filterModel.Filters.Find(x => x.Name == name);
    }

    private void OverwriteFilter(MediaFilterModel.ExternalFilterModel filter)
    {
        MediaFilterModel.ExternalModel? filterModel = _filterModel;
        if (filterModel is null)
        {
            return;
        }

        RemoveFilter(filter.Name);
        filterModel.Filters.Add(filter);
    }

    private void RemoveFilter(string name)
    {
        MediaFilterModel.ExternalModel? filterModel = _filterModel;
        if (filterModel is null)
        {
            return;
        }

        MediaFilterModel.ExternalFilterModel? oldFilter = filterModel.Filters.Find(x => x.Name == name);
        if (oldFilter is not null)
        {
            filterModel.Filters.Remove(oldFilter);
        }
    }

    public struct ExpressionTokenInfo
    {
        public string Text;
        public int CursorPosition;
    }
}
