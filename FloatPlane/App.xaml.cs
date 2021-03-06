﻿/* 
 * Copyright (C) 2017 Dominic Maas
 *
 * This software may be modified and distributed under the terms
 * of the MIT license.  See the LICENSE file for details.
 */

using System;
using System.IO;
using System.Threading;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Background;
using Windows.Networking.BackgroundTransfer;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Notifications;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using FloatPlane.Helpers;
using FloatPlane.Sources;
using FloatPlane.Views;
using Microsoft.HockeyApp;
using Microsoft.Toolkit.Uwp.Helpers;
using Microsoft.Toolkit.Uwp.Notifications;

namespace FloatPlane
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    sealed partial class App
    {
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();

            HockeyClient.Current.Configure("f9821c6805724641aaf6cc47c7a99f61");
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            // Do not repeat app initialization when the Window already has content,
            // just ensure that the window is active
            if (!(Window.Current.Content is Frame rootFrame))
            {
                // Create a Frame to act as the navigation context and navigate to the first page
                rootFrame = new Frame();

                // Place the frame in the current Window
                Window.Current.Content = rootFrame;
            }

            if (e.PrelaunchActivated == false)
            {
                if (rootFrame.Content == null)
                {
                    // When the navigation stack isn't restored navigate to the first page,
                    // configuring the new page by passing required information as a navigation
                    // parameter
                    rootFrame.Navigate(typeof(WelcomeView), e.Arguments);
                }

                SystemNavigationManager.GetForCurrentView().BackRequested +=
                    App_BackRequested;

                rootFrame.Navigated += RootFrame_Navigated;

                // Ensure the current window is active
                Window.Current.Activate();
            }


            var titleBar = ApplicationView.GetForCurrentView().TitleBar;

            // Set active window colors
            titleBar.ForegroundColor = Colors.White;
            titleBar.BackgroundColor = Color.FromArgb(255, 21, 21, 21);
            titleBar.ButtonBackgroundColor = Color.FromArgb(255, 21, 21, 21);

            HockeyClient.Current.TrackEvent("App Launch");

            // Background task runs every 60 minutes to look for fresh content
            BackgroundTaskHelper.Register("FloatPlane.ContentChecker", new TimeTrigger(60, false), true);
        }

        private void RootFrame_Navigated(object sender, NavigationEventArgs e)
        {
            Frame rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
                return;

            if (rootFrame.CanGoBack)
            {
                // Show UI in title bar if opted-in and in-app backstack is not empty.
                SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                    AppViewBackButtonVisibility.Visible;
            }
            else
            {
                // Remove the UI from the title bar if in-app back stack is empty.
                SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                    AppViewBackButtonVisibility.Collapsed;
            }
        }

        private void App_BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (!(Window.Current.Content is Frame rootFrame))
                return;

            // Navigate back if possible, and if the event has not 
            // already been handled .
            if (rootFrame.CanGoBack && e.Handled == false)
            {
                e.Handled = true;
                rootFrame.GoBack();
            }
        }

        protected override async void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            base.OnBackgroundActivated(args);

            var deferral = args.TaskInstance.GetDeferral();

            var helper = new LocalObjectStorageHelper();

            if (args.TaskInstance.Task.Name == "FloatPlane.ContentChecker")
            {
                // Process is enabled
                if (helper.KeyExists(EnableDownload) && helper.Read(EnableDownload, false))
                {
                    var lastCheckTime = helper.Read(LastCheckTime, DateTime.UtcNow).ToLocalTime();
                    var source = new RecentVideoSource();

                    var downloader = new BackgroundDownloader();

                    // Check that a folder has been picked
                    var folder = await Windows.Storage.AccessCache.StorageApplicationPermissions.FutureAccessList.GetFolderAsync(SaveLocationFolder);

                    if (folder != null)
                    {
                        // Get latest videos
                        var videos = await source.GetPagedItemsAsync(0, 10);

                        foreach (var videoModel in videos)
                        {
                            if (videoModel.Created <= lastCheckTime)
                                continue;

                            var fileName = videoModel.Title;

                            // Remove invalid characters from title to use as a file name
                            foreach (var c in Path.GetInvalidFileNameChars())
                            {
                                if (!fileName.Contains(c.ToString()))
                                    continue;

                                fileName = fileName.Remove(fileName.IndexOf(c), 1);
                            }

                            System.Diagnostics.Debug.WriteLine("NEW VIDEO: " + videoModel.Title);

                            var file = await folder.CreateFileAsync(fileName + ".mp4", CreationCollisionOption.OpenIfExists);

                            var remoteVideoFile = await VideoHelper.GetVideoStreamUrlAsync(videoModel, true);
                            var operation = downloader.CreateDownload(new Uri(remoteVideoFile), file);

                            // Construct the visuals of the toast
                            var toastContent = new ToastContent
                            {
                                Visual = new ToastVisual
                                {
                                    BindingGeneric = new ToastBindingGeneric
                                    {
                                        Children =
                                        {
                                            new AdaptiveText
                                            {
                                                Text = fileName
                                            },

                                            new AdaptiveText
                                            {
                                                Text = "New Video! Saving to your library :)"
                                            },

                                            new AdaptiveImage
                                            {
                                               Source = videoModel.ImageUrl
                                            }
                                        }
                                    }
                                }
                            };

                            // Show the toast
                            var toast = new ToastNotification(toastContent.GetXml());
                            ToastNotificationManager.CreateToastNotifier().Show(toast);

                            // Start downloading
                            var progressCallback = new Progress<DownloadOperation>(ProgressCallback);
                            await operation.StartAsync().AsTask(CancellationToken.None, progressCallback);
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("Folder is null");
                    }
                }
            }

            // Save the key
            helper.Save(LastCheckTime, DateTime.UtcNow);

            deferral.Complete();
        }

        private void ProgressCallback(DownloadOperation obj)
        {
            System.Diagnostics.Debug.WriteLine("Downloading... " + obj.Progress.BytesReceived / obj.Progress.TotalBytesToReceive * 100);
        }

        public static string LastCheckTime = "LastCheckTime";
        public static string EnableDownload = "EnableDownload";
        public static string SaveLocationFolder = "SaveLocation";
    }
}
