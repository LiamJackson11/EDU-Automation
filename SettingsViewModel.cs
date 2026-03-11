// ViewModels/SettingsViewModel.cs + Converters/Converters.cs
// All using directives are at the top of the file.

using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EduAutomation.Services;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace EduAutomation.ViewModels
{
    public partial class SettingsViewModel : BaseViewModel
    {
        private readonly ISecureConfigService _config;
        private readonly ICanvasService _canvas;
        private readonly IOpenAIService _openAi;

        [ObservableProperty] private string _canvasBaseUrl = string.Empty;
        [ObservableProperty] private string _canvasApiToken = string.Empty;
        [ObservableProperty] private string _openAiApiKey = string.Empty;
        [ObservableProperty] private string _icBaseUrl = string.Empty;
        [ObservableProperty] private string _icUsername = string.Empty;
        [ObservableProperty] private string _icPassword = string.Empty;
        [ObservableProperty] private string _validationStatus = string.Empty;
        [ObservableProperty] private bool _isCanvasValid = false;
        [ObservableProperty] private bool _isOpenAiValid = false;
        [ObservableProperty] private bool _isConfigComplete = false;

        public SettingsViewModel(
            ISecureConfigService config,
            ICanvasService canvas,
            IOpenAIService openAi,
            ILoggingService log) : base(log)
        {
            _config = config;
            _canvas = canvas;
            _openAi = openAi;
        }

        [RelayCommand]
        public async Task LoadSettingsAsync()
        {
            CanvasBaseUrl = await _config.GetCanvasBaseUrlAsync() ?? string.Empty;
            IcBaseUrl = await _config.GetInfiniteCampusBaseUrlAsync() ?? string.Empty;
            IcUsername = await _config.GetInfiniteCampusUsernameAsync() ?? string.Empty;
            IsConfigComplete = await _config.IsConfigurationCompleteAsync();
            Log.LogInfo("SettingsViewModel", "Settings loaded from SecureStorage.");
        }

        [RelayCommand]
        public async Task SaveAndValidateAsync()
        {
            await RunSafeAsync(async () =>
            {
                Log.LogInfo("SettingsViewModel", "Saving credentials...");
                if (!string.IsNullOrWhiteSpace(CanvasBaseUrl))
                    await _config.SaveCanvasBaseUrlAsync(CanvasBaseUrl);
                if (!string.IsNullOrWhiteSpace(CanvasApiToken))
                    await _config.SaveCanvasApiTokenAsync(CanvasApiToken);
                if (!string.IsNullOrWhiteSpace(OpenAiApiKey))
                    await _config.SaveOpenAiApiKeyAsync(OpenAiApiKey);
                if (!string.IsNullOrWhiteSpace(IcBaseUrl))
                    await _config.SaveInfiniteCampusBaseUrlAsync(IcBaseUrl);
                if (!string.IsNullOrWhiteSpace(IcUsername))
                    await _config.SaveInfiniteCampusUsernameAsync(IcUsername);
                if (!string.IsNullOrWhiteSpace(IcPassword))
                    await _config.SaveInfiniteCampusPasswordAsync(IcPassword);

                ValidationStatus = "Validating API connections...";
                IsCanvasValid = await _canvas.ValidateTokenAsync();
                IsOpenAiValid = await _openAi.ValidateApiKeyAsync();
                IsConfigComplete = await _config.IsConfigurationCompleteAsync();
                ValidationStatus = IsCanvasValid && IsOpenAiValid
                    ? "All connections validated successfully!"
                    : $"Canvas: {(IsCanvasValid ? "OK" : "FAILED")} | OpenAI: {(IsOpenAiValid ? "OK" : "FAILED")}";

                Log.LogInfo("SettingsViewModel",
                    $"Credential save complete. Canvas valid: {IsCanvasValid}, OpenAI valid: {IsOpenAiValid}");
            }, "Saving and validating...");
        }

        [RelayCommand]
        public async Task ClearAllDataAsync()
        {
            await _config.ClearAllCredentialsAsync();
            CanvasBaseUrl = string.Empty;
            CanvasApiToken = string.Empty;
            OpenAiApiKey = string.Empty;
            IcBaseUrl = string.Empty;
            IcUsername = string.Empty;
            IcPassword = string.Empty;
            IsConfigComplete = false;
            ValidationStatus = "All credentials cleared.";
            Log.LogInfo("SettingsViewModel", "All credentials cleared by user.");
        }
    }
}

namespace EduAutomation.Converters
{
    public class BoolToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool boolValue = value is bool b && b;
            return boolValue ? Colors.LimeGreen : Colors.Gray;
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class NotNullConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value != null;
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class NullConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value == null;
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class InvertBoolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is bool b && !b;
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is bool b && !b;
    }
}