using LibreSpotUWP.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace LibreSpotUWP.Services
{
    public sealed class DownloadTrackerService
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, TrackDownloadStatus> _trackStates = new Dictionary<string, TrackDownloadStatus>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DownloadGroupStatus> _groupStates = new Dictionary<string, DownloadGroupStatus>(StringComparer.OrdinalIgnoreCase);

        public event EventHandler<TrackDownloadStatus> TrackStatusChanged;
        public event EventHandler<DownloadGroupStatus> GroupStatusChanged;

        public string BeginGroup(string title, int totalTracks)
        {
            var groupId = Guid.NewGuid().ToString("N");
            DownloadGroupStatus snapshot;

            lock (_gate)
            {
                snapshot = new DownloadGroupStatus
                {
                    GroupId = groupId,
                    Title = title,
                    TotalTracks = Math.Max(1, totalTracks),
                    UpdatedAt = DateTimeOffset.Now
                };
                _groupStates[groupId] = snapshot;
            }

            RaiseGroupChanged(snapshot);
            return groupId;
        }

        public void TrackQueued(string groupId, string trackUri, string trackName)
        {
            UpdateTrackState(groupId, trackUri, trackName, DownloadTrackState.Queued, null, incrementActive: false);
        }

        public void TrackStarted(string groupId, string trackUri, string trackName)
        {
            UpdateTrackState(groupId, trackUri, trackName, DownloadTrackState.Downloading, null, incrementActive: true);
        }

        public void TrackCompleted(string groupId, string trackUri, string trackName)
        {
            UpdateTrackState(groupId, trackUri, trackName, DownloadTrackState.Completed, null, incrementActive: false, incrementCompleted: true);
        }

        public void TrackFailed(string groupId, string trackUri, string trackName, string errorMessage)
        {
            UpdateTrackState(groupId, trackUri, trackName, DownloadTrackState.Failed, errorMessage, incrementActive: false, incrementFailed: true);
        }

        public void ClearTrack(string trackUri)
        {
            TrackDownloadStatus snapshot = null;
            lock (_gate)
            {
                if (_trackStates.Remove(trackUri))
                {
                    snapshot = new TrackDownloadStatus
                    {
                        TrackUri = trackUri,
                        State = DownloadTrackState.Idle
                    };
                }
            }

            if (snapshot != null)
                TrackStatusChanged?.Invoke(this, snapshot);
        }

        public TrackDownloadStatus GetTrackStatus(string trackUri)
        {
            lock (_gate)
            {
                if (trackUri != null && _trackStates.TryGetValue(trackUri, out var status))
                {
                    return Clone(status);
                }
            }

            return new TrackDownloadStatus
            {
                TrackUri = trackUri,
                State = DownloadTrackState.Idle
            };
        }

        private void UpdateTrackState(
            string groupId,
            string trackUri,
            string trackName,
            DownloadTrackState state,
            string errorMessage,
            bool incrementActive,
            bool incrementCompleted = false,
            bool incrementFailed = false)
        {
            TrackDownloadStatus trackSnapshot;
            DownloadGroupStatus groupSnapshot = null;

            lock (_gate)
            {
                trackSnapshot = new TrackDownloadStatus
                {
                    GroupId = groupId,
                    TrackUri = trackUri,
                    TrackName = trackName,
                    State = state,
                    ErrorMessage = errorMessage
                };

                _trackStates[trackUri] = trackSnapshot;

                if (!string.IsNullOrWhiteSpace(groupId) && _groupStates.TryGetValue(groupId, out var group))
                {
                    if (incrementActive)
                        group.ActiveTracks += 1;

                    if (state == DownloadTrackState.Completed || state == DownloadTrackState.Failed)
                        group.ActiveTracks = Math.Max(0, group.ActiveTracks - 1);

                    if (incrementCompleted)
                        group.CompletedTracks += 1;

                    if (incrementFailed)
                        group.FailedTracks += 1;

                    group.UpdatedAt = DateTimeOffset.Now;
                    groupSnapshot = Clone(group);

                    if (groupSnapshot.IsFinished)
                        _groupStates.Remove(groupId);
                }
            }

            TrackStatusChanged?.Invoke(this, Clone(trackSnapshot));

            if (groupSnapshot != null)
                RaiseGroupChanged(groupSnapshot);
        }

        private void RaiseGroupChanged(DownloadGroupStatus group)
        {
            var snapshot = Clone(group);
            ShowOrUpdateToast(snapshot);
            GroupStatusChanged?.Invoke(this, snapshot);
        }

        private static TrackDownloadStatus Clone(TrackDownloadStatus status)
        {
            return new TrackDownloadStatus
            {
                GroupId = status.GroupId,
                TrackUri = status.TrackUri,
                TrackName = status.TrackName,
                State = status.State,
                ErrorMessage = status.ErrorMessage
            };
        }

        private static DownloadGroupStatus Clone(DownloadGroupStatus status)
        {
            return new DownloadGroupStatus
            {
                GroupId = status.GroupId,
                Title = status.Title,
                TotalTracks = status.TotalTracks,
                CompletedTracks = status.CompletedTracks,
                FailedTracks = status.FailedTracks,
                ActiveTracks = status.ActiveTracks,
                UpdatedAt = status.UpdatedAt
            };
        }

        private static void ShowOrUpdateToast(DownloadGroupStatus group)
        {
            try
            {
                var notifier = ToastNotificationManager.CreateToastNotifier();
                if (group.IsFinished)
                {
                    ToastNotificationManager.History.Remove(group.GroupId, "downloads");
                    notifier.Show(new ToastNotification(BuildCompletionToastXml(group))
                    {
                        Tag = $"{group.GroupId}-complete",
                        Group = "downloads"
                    });
                    return;
                }

                var updateData = BuildProgressNotificationData(group, sequenceNumber: 1);
                if (notifier.Update(updateData, group.GroupId, "downloads") != NotificationUpdateResult.Succeeded)
                {
                    var toast = new ToastNotification(BuildProgressToastXml(group))
                    {
                        Tag = group.GroupId,
                        Group = "downloads",
                        Data = BuildProgressNotificationData(group, sequenceNumber: 0)
                    };

                    notifier.Show(toast);
                }
            }
            catch (Exception ex)
            {
                LogService.Error(ex, "Failed to show download toast");
            }
        }

        private static XmlDocument BuildProgressToastXml(DownloadGroupStatus group)
        {
            var xml = new XmlDocument();
            xml.LoadXml(
                "<toast scenario=\"reminder\">" +
                "<visual><binding template=\"ToastGeneric\">" +
                $"<text>{Escape(group.Title)}</text>" +
                "<text>Downloading music</text>" +
                "<progress title='{progressTitle}' value='{progressValue}' valueStringOverride='{progressValueString}' status='{progressStatus}'/>" +
                "</binding></visual></toast>");
            return xml;
        }

        private static XmlDocument BuildCompletionToastXml(DownloadGroupStatus group)
        {
            var summary = group.FailedTracks > 0
                ? $"Downloaded {group.CompletedTracks}/{group.TotalTracks} tracks, {group.FailedTracks} failed."
                : $"Downloaded {group.CompletedTracks}/{group.TotalTracks} tracks.";

            var xml = new XmlDocument();
            xml.LoadXml(
                "<toast><visual><binding template=\"ToastGeneric\">" +
                $"<text>{Escape(group.Title)}</text>" +
                $"<text>{Escape(summary)}</text>" +
                "</binding></visual></toast>");
            return xml;
        }

        private static NotificationData BuildProgressNotificationData(DownloadGroupStatus group, uint sequenceNumber)
        {
            var values = new Dictionary<string, string>
            {
                ["progressTitle"] = "Total Progress",
                ["progressValue"] = ((double)group.CompletedTracks / Math.Max(1, group.TotalTracks))
                    .ToString("0.###", CultureInfo.InvariantCulture),
                ["progressValueString"] = $"{group.CompletedTracks}/{group.TotalTracks} tracks",
                ["progressStatus"] = group.FailedTracks > 0
                    ? $"{group.FailedTracks} failed"
                    : group.ActiveTracks > 0 ? "Downloading" : "Queued"
            };

            return new NotificationData(values)
            {
                SequenceNumber = sequenceNumber
            };
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }
    }
}
