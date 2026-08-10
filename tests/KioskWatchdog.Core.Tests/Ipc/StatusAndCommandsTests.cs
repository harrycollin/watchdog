using System.Text.Json;
using System.Text.Json.Serialization;
using KioskWatchdog.Core.Ipc;
using KioskWatchdog.Core.Status;

namespace KioskWatchdog.Core.Tests.Ipc;

public class StatusAndCommandsTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void Status_envelope_round_trips_with_application_ids()
    {
        var path = Path.Combine(Path.GetTempPath(), "kw-status-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new WatchdogStatusStore();
            store.Upsert("egl1", s =>
            {
                s.ApplicationName = "EGL 1";
                s.Status = ApplicationStatus.Running;
                s.ProcessId = 111;
            });
            store.Upsert("egl2", s =>
            {
                s.ApplicationName = "EGL 2";
                s.Status = ApplicationStatus.Stopped;
            });

            using (var publisher = new StatusFilePublisher(store, path))
            {
                publisher.Publish();
            }

            var snapshot = StatusFilePublisher.ReadSnapshot(path);
            Assert.NotNull(snapshot);
            Assert.Equal(2, snapshot!.Applications.Count);
            Assert.Contains(snapshot.Applications, a => a.Id == "egl1" && a.ProcessId == 111);
            Assert.Contains(snapshot.Applications, a => a.Id == "egl2" && a.Status == ApplicationStatus.Stopped);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(doc.RootElement.TryGetProperty("applications", out _));
            Assert.False(doc.RootElement.TryGetProperty("status", out _));
            Assert.False(doc.RootElement.TryGetProperty("processId", out _));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Single_app_status_stays_inside_applications_array()
    {
        var path = Path.Combine(Path.GetTempPath(), "kw-status-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new WatchdogStatusStore();
            store.Upsert("default", s =>
            {
                s.ApplicationName = "Only";
                s.Status = ApplicationStatus.Running;
                s.ProcessId = 42;
            });

            using (var publisher = new StatusFilePublisher(store, path))
            {
                publisher.Publish();
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.False(doc.RootElement.TryGetProperty("status", out _));
            Assert.Equal(1, doc.RootElement.GetProperty("applications").GetArrayLength());

            var single = StatusFilePublisher.Read(path);
            Assert.NotNull(single);
            Assert.Equal(42, single!.ProcessId);
            Assert.Equal("default", single.Id);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Command_queue_writes_application_id()
    {
        var path = Path.Combine(Path.GetTempPath(), "kw-cmd-" + Guid.NewGuid() + ".json");
        try
        {
            var queue = new CommandFileQueue(path);
            queue.Enqueue(WatchdogCommandType.Restart, "egl1");

            var json = File.ReadAllText(path);
            var command = JsonSerializer.Deserialize<WatchdogCommand>(json, Options);
            Assert.NotNull(command);
            Assert.Equal(WatchdogCommandType.Restart, command!.Type);
            Assert.Equal("egl1", command.ApplicationId);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Command_without_application_id_omits_or_nulls_field()
    {
        var path = Path.Combine(Path.GetTempPath(), "kw-cmd-" + Guid.NewGuid() + ".json");
        try
        {
            var queue = new CommandFileQueue(path);
            queue.Enqueue(WatchdogCommandType.Start);

            var json = File.ReadAllText(path);
            var command = JsonSerializer.Deserialize<WatchdogCommand>(json, Options);
            Assert.NotNull(command);
            Assert.Equal(WatchdogCommandType.Start, command!.Type);
            Assert.True(string.IsNullOrEmpty(command.ApplicationId));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
