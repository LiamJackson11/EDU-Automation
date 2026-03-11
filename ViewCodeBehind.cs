// Views/ViewCodeBehind.cs
// Code-behind for all five tab pages.
// All using directives are at the top of the file.

using System.Threading.Tasks;
using EduAutomation.Models;
using EduAutomation.ViewModels;
using Microsoft.Maui.Controls;

namespace EduAutomation.Views
{
    // ============================================================
    // DashboardPage
    // ============================================================

    public partial class DashboardPage : ContentPage
    {
        public DashboardPage(DashboardViewModel viewModel)
        {
            InitializeComponent();
            BindingContext = viewModel;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            if (BindingContext is DashboardViewModel vm)
                await vm.RefreshDashboardCommand.ExecuteAsync(null);
        }
    }

    // ============================================================
    // GmailPage
    // ============================================================

    public partial class GmailPage : ContentPage
    {
        public GmailPage(GmailViewModel viewModel)
        {
            InitializeComponent();
            BindingContext = viewModel;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            // If not yet authenticated, the Connect button in the UI handles the flow.
            if (BindingContext is GmailViewModel vm && vm.IsAuthenticated)
                await vm.LoadEmailsCommand.ExecuteAsync(null);
        }
    }

    // ============================================================
    // AssignmentsPage
    // ============================================================

    public partial class AssignmentsPage : ContentPage
    {
        private readonly AssignmentsViewModel _viewModel;
        private readonly ReviewViewModel _reviewViewModel;

        public AssignmentsPage(AssignmentsViewModel viewModel, ReviewViewModel reviewViewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _reviewViewModel = reviewViewModel;
            BindingContext = viewModel;
            viewModel.ReviewItemReady += OnReviewItemReady;
        }

        private async void OnReviewItemReady(ReviewItem item)
        {
            _reviewViewModel.AddReviewItem(item);
            await Shell.Current.GoToAsync("//review");
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            if (_viewModel.MissingAssignments.Count == 0)
                await _viewModel.LoadMissingAssignmentsCommand.ExecuteAsync(null);
        }
    }

    // ============================================================
    // DataDumpPage
    // ============================================================

    public partial class DataDumpPage : ContentPage
    {
        private readonly DataDumpViewModel _viewModel;
        private readonly ReviewViewModel _reviewViewModel;

        public DataDumpPage(DataDumpViewModel viewModel, ReviewViewModel reviewViewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _reviewViewModel = reviewViewModel;
            BindingContext = viewModel;
            viewModel.ReviewItemReady += OnReviewItemReady;
        }

        private async void OnReviewItemReady(ReviewItem item)
        {
            _reviewViewModel.AddReviewItem(item);
            await Shell.Current.GoToAsync("//review");
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            if (_viewModel.AvailableAssignments.Count == 0)
                await _viewModel.LoadAssignmentsCommand.ExecuteAsync(null);
        }
    }

    // ============================================================
    // ReviewPage
    // ============================================================

    public partial class ReviewPage : ContentPage
    {
        public ReviewPage(ReviewViewModel viewModel)
        {
            InitializeComponent();
            BindingContext = viewModel;
        }
    }

    // ============================================================
    // SettingsPage
    // ============================================================

    public partial class SettingsPage : ContentPage
    {
        public SettingsPage(SettingsViewModel viewModel)
        {
            InitializeComponent();
            BindingContext = viewModel;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            if (BindingContext is SettingsViewModel vm)
                await vm.LoadSettingsCommand.ExecuteAsync(null);
        }
    }
}