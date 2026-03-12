// ViewModels/SettingsViewModel.cs + Converters/Converters.cs
// Updated to use username/password for Canvas (no API token).
// All using directives are at the top of the file.

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EduAutomation.Services;

namespace EduAutomation.ViewModels
{
    public partial class SettingsViewModel : BaseViewModel
    {
        private readonly ISecureConfigService    _config;
        private readonly ICanvasService          _canvas;
        private readonly IInfiniteCampusService  _ic;
        private readonly IOpenAIService          _openAi;

        // Canvas - username/password only, no API token.
        [ObservableProperty] private string _canvasBaseUrl  = string.Empty;
        [ObservableProperty] private string _canvasUsername = string.Empty;
        [ObservableProperty] private string _canvasPassword = string.Empty;

        // Infinite Campus
        [ObservableProperty] private string _icBaseUrl  = string.Empty;
        [ObservableProperty] private string _icUsername = string.Empty;
        [ObservableProperty] private string _icPassword = string.Empty;

        // OpenAI
        [ObservableProperty] private string _openAiApiKey = string.Empty;

        [ObservableProperty] private string _validationStatus  = string.Empty;
        [ObservableProperty] private bool   _isCanvasValid     = false;
        [ObservableProperty] private bool   _isIcValid         = false;
        [ObservableProperty] private bool   _isOpenAiValid     = false;
        [ObservableProperty] private bool   _isConfigComplete  = false;

        public SettingsViewModel(
            ISecureConfigService    config,
            ICanvasService          canvas,
            IInfiniteCampusService  ic,
            IOpenAIService          openAi,
            ILoggingService         log) : base(log)
        {
            _config  = config;
            _canvas  = canvas;
            _ic      = ic;
            _openAi  = openAi;
        }

        [RelayCommand]
        public async Task LoadSettingsAsync()
        {
            // BUG FIX: Previously LoadSettingsAsync loaded URL and username only,
            // leaving CanvasPassword, IcPassword, and OpenAiApiKey always blank
            // when the Settings page appeared. Users had to re-type their password
            // and API key on every visit even though credentials were saved.
            CanvasBaseUrl  = await _config.GetCanvasBaseUrlAsync()              ?? string.Empty;
            CanvasUsername = await _config.GetCanvasUsernameAsync()             ?? string.Empty;
            CanvasPassword = await _config.GetCanvasPasswordAsync()             ?? string.Empty;
            IcBaseUrl      = await _config.GetInfiniteCampusBaseUrlAsync()      ?? string.Empty;
            IcUsername     = await _config.GetInfiniteCampusUsernameAsync()     ?? string.Empty;
            IcPassword     = await _config.GetInfiniteCampusPasswordAsync()     ?? string.Empty;
            OpenAiApiKey   = await _config.GetOpenAiApiKeyAsync()               ?? string.Empty;
            IsConfigComplete = await _config.IsConfigurationCompleteAsync();
            Log.LogInfo("SettingsViewModel", "Settings loaded.");
        }

        [RelayCommand]
        public async Task SaveAndValidateAsync()
        {
            await RunSafeAsync(async () =>
            {
                // Save only non-empty fields so we do not overwrite with blank.
                if (!string.IsNullOrWhiteSpace(CanvasBaseUrl))
                    await _config.SaveCanvasBaseUrlAsync(CanvasBaseUrl);
                if (!string.IsNullOrWhiteSpace(CanvasUsername))
                    await _config.SaveCanvasUsernameAsync(CanvasUsername);
                if (!string.IsNullOrWhiteSpace(CanvasPassword))
                    await _config.SaveCanvasPasswordAsync(CanvasPassword);
                if (!string.IsNullOrWhiteSpace(IcBaseUrl))
                    await _config.SaveInfiniteCampusBaseUrlAsync(IcBaseUrl);
                if (!string.IsNullOrWhiteSpace(IcUsername))
                    await _config.SaveInfiniteCampusUsernameAsync(IcUsername);
                if (!string.IsNullOrWhiteSpace(IcPassword))
                    await _config.SaveInfiniteCampusPasswordAsync(IcPassword);
                if (!string.IsNullOrWhiteSpace(OpenAiApiKey))
                    await _config.SaveOpenAiApiKeyAsync(OpenAiApiKey);

                ValidationStatus = "Validating connections...";
                Log.LogInfo("SettingsViewModel", "Testing Canvas login...");
                IsCanvasValid = await _canvas.LoginAsync();

                Log.LogInfo("SettingsViewModel", "Testing Infinite Campus login...");
                IsIcValid = await _ic.LoginAsync();

                Log.LogInfo("SettingsViewModel", "Testing OpenAI key...");
                IsOpenAiValid = await _openAi.ValidateApiKeyAsync();

                IsConfigComplete = await _config.IsConfigurationCompleteAsync();
                ValidationStatus =
                    $"Canvas: {(IsCanvasValid ? "Connected" : "FAILED")}  |  " +
                    $"IC: {(IsIcValid ? "Connected" : "FAILED")}  |  " +
                    $"OpenAI: {(IsOpenAiValid ? "Connected" : "FAILED")}";

                Log.LogInfo("SettingsViewModel",
                    $"Validation done. Canvas={IsCanvasValid} IC={IsIcValid} OpenAI={IsOpenAiValid}");
            }, "Saving and validating...");
        }

        [RelayCommand]
        public async Task ClearAllDataAsync()
        {
            await _config.ClearAllCredentialsAsync();
            CanvasBaseUrl  = CanvasUsername = CanvasPassword = string.Empty;
            IcBaseUrl      = IcUsername     = IcPassword     = string.Empty;
            OpenAiApiKey   = string.Empty;
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
            bool b = value is bool bv && bv;
            return b ? Colors.LimeGreen : Colors.Gray;
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