// ViewModels/BaseViewModel.cs
// Base class for all ViewModels. Provides ObservableObject from CommunityToolkit.Mvvm,
// IsBusy state tracking, and unified error display.

using CommunityToolkit.Mvvm.ComponentModel;
using EduAutomation.Services;

namespace EduAutomation.ViewModels
{
    public partial class BaseViewModel : ObservableObject
    {
        protected readonly ILoggingService Log;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotBusy))]
        private bool _isBusy = false;

        [ObservableProperty]
        private string _busyMessage = "Loading...";

        [ObservableProperty]
        private string _errorMessage = string.Empty;

        [ObservableProperty]
        private bool _hasError = false;

        public bool IsNotBusy => !IsBusy;

        protected BaseViewModel(ILoggingService log)
        {
            Log = log;
        }

        protected void SetBusy(bool isBusy, string message = "Loading...")
        {
            IsBusy      = isBusy;
            BusyMessage = message;

            // BUG FIX: Removed "if (!isBusy) ErrorMessage = string.Empty;" from here.
            // Previously this was clearing ErrorMessage every time SetBusy(false) was
            // called — including from RunSafeAsync's finally block — which wiped out
            // the error that ShowError had just set, so errors were never visible.
            // ErrorMessage is now cleared explicitly only in ClearError().
        }

        protected void ShowError(string message)
        {
            ErrorMessage = message;
            HasError     = true;
            IsBusy       = false;
            Log.LogError(GetType().Name, $"UI Error shown to user: {message}");
        }

        protected void ClearError()
        {
            ErrorMessage = string.Empty;
            HasError     = false;
        }

        // Executes a task safely, showing errors to the user and logging exceptions.
        protected async Task RunSafeAsync(
            Func<Task> action,
            string busyMessage = "Loading...",
            string? errorPrefix = null)
        {
            if (IsBusy) return;
            SetBusy(true, busyMessage);
            ClearError();
            try
            {
                await action();
            }
            catch (Exceptions.ServiceUnavailableException ex)
            {
                ShowError($"{errorPrefix ?? ex.ServiceName} is unavailable: {ex.Message}");
            }
            catch (Exceptions.AiHallucinationGuardException ex)
            {
                ShowError($"AI Guard blocked response: {ex.Message}");
            }
            catch (Exceptions.UnauthorizedSubmissionException ex)
            {
                ShowError($"Submission blocked: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                ShowError("The operation was cancelled.");
            }
            catch (Exception ex)
            {
                Log.LogError(GetType().Name, "Unhandled exception in RunSafeAsync.", ex);
                ShowError($"An unexpected error occurred: {ex.Message}");
            }
            finally
            {
                // BUG FIX: Only stop the spinner; do NOT call SetBusy(false) which
                // previously cleared ErrorMessage. We set IsBusy directly so the
                // error message set by ShowError is preserved for the UI to display.
                IsBusy = false;
            }
        }
    }
}