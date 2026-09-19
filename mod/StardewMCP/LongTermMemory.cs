using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;

namespace StardewMCP;

public partial class CommandExecutor
{
    private const string NotebookKey = "notebook-v1";
    private const string TaskKey = "tasks-v1";
    private const string ChestMemoryIdKey = "YourName.StardewMCP/MemoryId";
    private NotebookDocument _notebook = new();
    private TaskDocument _tasks = new();
    private bool _memoryLoaded;
    private bool _notebookDirty;
    private bool _tasksDirty;

    public void LoadLongTermMemory()
    {
        _notebook = LoadWithBackup(NotebookKey, MemorySchema.Normalize, () => new NotebookDocument());
        _tasks = LoadWithBackup(TaskKey, MemorySchema.Normalize, () => new TaskDocument());
        _memoryLoaded = true;
        // Re-save normalized/recovered data so migrations and backup recovery become durable.
        _notebookDirty = true;
        _tasksDirty = true;
        RefreshAllLoadedChests();
        FlushLongTermMemory();
        _monitor.Log($"[MEMORY] Loaded {_notebook.Chests.Count} chests and {_tasks.Tasks.Count} tasks.", LogLevel.Info);
    }

    public void FlushLongTermMemory()
    {
        if (!_memoryLoaded) return;
        if (_notebookDirty)
        {
            try
            {
                SaveWithBackup(NotebookKey, _notebook, MemorySchema.Normalize);
                _notebookDirty = false;
            }
            catch { /* SaveWithBackup logged the error. Keep dirty for the next safe retry. */ }
        }
        if (_tasksDirty)
        {
            try
            {
                SaveWithBackup(TaskKey, _tasks, MemorySchema.Normalize);
                _tasksDirty = false;
            }
            catch { /* SaveWithBackup logged the error. Keep dirty for the next safe retry. */ }
        }
    }

    public void ClearLongTermMemorySession()
    {
        _memoryLoaded = false;
        _notebook = new();
        _tasks = new();
        _notebookDirty = false;
        _tasksDirty = false;
    }

    public void RefreshCurrentLocationChestMemory()
    {
        if (!_memoryLoaded || !Context.IsWorldReady || Game1.currentLocation == null) return;
        RefreshLocationChests(Game1.currentLocation);
        FlushLongTermMemory();
    }

    public IReadOnlyList<PersistentTask> GetPersistentTasks(string? status = null)
    {
        if (!_memoryLoaded) return Array.Empty<PersistentTask>();
        IEnumerable<PersistentTask> selected = status == null
            ? _tasks.Tasks.ToList()
            : _tasks.Tasks.Where(p => p.Status.Equals(status, StringComparison.OrdinalIgnoreCase)).ToList();
        return selected.Select(CloneTask).ToList();
    }

    public IReadOnlyList<ChestMemory> GetChestMemories() =>
        !_memoryLoaded ? Array.Empty<ChestMemory>() : Clone(_notebook.Chests);

    public ChestMemory SetChestPurpose(string chestId, string purpose, string source = "user", double confidence = 1)
    {
        if (!_memoryLoaded) throw new InvalidOperationException("Long-term memory is not loaded.");
        ChestMemory chest = _notebook.Chests.FirstOrDefault(p => p.Id.Equals(chestId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Unknown remembered chest id.");
        chest.Purpose = new ChestPurposeMemory
        {
            Value = purpose?.Trim() ?? "",
            Source = string.IsNullOrWhiteSpace(source) ? "user" : source.Trim().ToLowerInvariant(),
            Confidence = Math.Clamp(confidence, 0, 1)
        };
        _notebookDirty = true;
        FlushLongTermMemory();
        return Clone(chest);
    }

    public MemoryNote UpsertMemoryNote(MemoryNote note)
    {
        if (!_memoryLoaded) throw new InvalidOperationException("Long-term memory is not loaded.");
        if (string.IsNullOrWhiteSpace(note.Text)) throw new InvalidOperationException("A memory note cannot be empty.");
        if (string.IsNullOrWhiteSpace(note.Id)) note.Id = Guid.NewGuid().ToString("N");
        string now = DateTime.UtcNow.ToString("O");
        MemoryNote stored = Clone(note);
        int index = _notebook.Notes.FindIndex(p => p.Id.Equals(stored.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            stored.CreatedAtUtc = _notebook.Notes[index].CreatedAtUtc;
            _notebook.Notes[index] = stored;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(stored.CreatedAtUtc)) stored.CreatedAtUtc = now;
            _notebook.Notes.Add(stored);
        }
        stored.UpdatedAtUtc = now;
        _notebookDirty = true;
        FlushLongTermMemory();
        return Clone(stored);
    }

    public PersistentTask UpsertPersistentTask(PersistentTask task)
    {
        if (!_memoryLoaded) throw new InvalidOperationException("Long-term memory is not loaded.");
        task = CloneTask(task);
        if (string.IsNullOrWhiteSpace(task.Id)) task.Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(task.Kind) || task.Kind.Length > 100) throw new InvalidOperationException("A persistent task requires a kind of 1..100 characters.");
        if (string.IsNullOrWhiteSpace(task.Summary) || task.Summary.Length > 1000) throw new InvalidOperationException("A persistent task requires a summary of 1..1000 characters.");
        if (task.Priority is < -100 or > 100) throw new InvalidOperationException("Task priority must be -100..100.");
        task.Status = MemorySchema.NormalizeStatus(task.Status);
        task.Targets ??= new();
        task.ResumePolicy ??= new();
        task.ResumePolicy.Mode = MemorySchema.NormalizeResumeMode(task.ResumePolicy.Mode);
        task.Metadata ??= new(StringComparer.OrdinalIgnoreCase);
        string now = DateTime.UtcNow.ToString("O");
        if (string.IsNullOrWhiteSpace(task.CreatedAtUtc)) task.CreatedAtUtc = now;
        if (string.IsNullOrWhiteSpace(task.CreatedGameDate)) task.CreatedGameDate = Context.IsWorldReady ? FarmDate() : "";
        if (task.CreatedGameTime == 0 && Context.IsWorldReady) task.CreatedGameTime = Game1.timeOfDay;
        task.UpdatedAtUtc = now;
        if (Context.IsWorldReady)
        {
            task.UpdatedGameDate = FarmDate();
            task.UpdatedGameTime = Game1.timeOfDay;
        }
        int index = _tasks.Tasks.FindIndex(p => p.Id.Equals(task.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            string previous = _tasks.Tasks[index].Status;
            if (!MemorySchema.CanTransition(previous, task.Status))
                throw new InvalidOperationException($"Task status cannot transition from {previous} to {task.Status}.");
            _tasks.Tasks[index] = task;
        }
        else _tasks.Tasks.Add(task);
        _tasksDirty = true;
        FlushLongTermMemory();
        return CloneTask(task);
    }

    public PersistentTask TransitionPersistentTask(string id, string status, string reason = "")
    {
        PersistentTask task = _tasks.Tasks.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Unknown persistent task id.");
        status = MemorySchema.NormalizeStatus(status);
        if (!MemorySchema.CanTransition(task.Status, status))
            throw new InvalidOperationException($"Task status cannot transition from {task.Status} to {status}.");
        PersistentTask updated = CloneTask(task);
        updated.Status = status;
        updated.BlockedReason = status == TaskStatuses.Blocked ? reason : "";
        return UpsertPersistentTask(updated);
    }

    private T LoadWithBackup<T>(string key, Func<T?, T> normalize, Func<T> create) where T : MemoryDocument
    {
        Exception? primaryError = null;
        try
        {
            T? value = _helper.Data.ReadSaveData<T>(key);
            if (value != null) return normalize(value);
        }
        catch (Exception ex) { primaryError = ex; }
        try
        {
            T? backup = _helper.Data.ReadSaveData<T>(key + "-backup");
            if (backup != null)
            {
                T recovered = normalize(backup);
                _monitor.Log($"[MEMORY] Recovered {key} from backup after primary read failure: {primaryError?.Message ?? "missing primary"}", LogLevel.Warn);
                return recovered;
            }
            if (primaryError != null)
                _monitor.Log($"[MEMORY] Could not load {key}, and no backup exists: {primaryError.Message}", LogLevel.Error);
        }
        catch (Exception backupError)
        {
            _monitor.Log($"[MEMORY] Could not load {key} or its backup: {primaryError?.Message}; {backupError.Message}", LogLevel.Error);
        }
        return normalize(create());
    }

    private void SaveWithBackup<T>(string key, T document, Func<T?, T> normalize) where T : MemoryDocument
    {
        try
        {
            T? previous = null;
            try
            {
                T? candidate = _helper.Data.ReadSaveData<T>(key);
                if (candidate != null) previous = normalize(candidate);
            }
            catch (Exception ex) { _monitor.Log($"[MEMORY] Existing {key} is unreadable; retaining prior backup: {ex.Message}", LogLevel.Warn); }
            if (previous != null) _helper.Data.WriteSaveData(key + "-backup", previous);
            document.SchemaVersion = MemorySchema.CurrentVersion;
            document.Revision++;
            document.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
            _helper.Data.WriteSaveData(key, document);
            T? verified = _helper.Data.ReadSaveData<T>(key);
            if (verified == null || normalize(verified).Revision != document.Revision)
                throw new InvalidOperationException("Save verification returned a different revision.");
        }
        catch (Exception ex)
        {
            _monitor.Log($"[MEMORY] Failed to safely write {key}: {ex.Message}", LogLevel.Error);
            throw;
        }
    }

    private void RefreshAllLoadedChests()
    {
        if (!Context.IsWorldReady) return;
        foreach (GameLocation location in Game1.locations)
            RefreshLocationChests(location);
    }

    private void RefreshLocationChests(GameLocation location)
    {
        foreach (var entry in location.Objects.Pairs)
            if (entry.Value is Chest chest && IsRegularPlayerChest(chest))
                RememberChest(location.Name, (int)entry.Key.X, (int)entry.Key.Y, chest);
    }

    private static bool IsRegularPlayerChest(Chest chest)
    {
        if (!ShopBool(ShopMember(chest, "playerChest")) || ShopBool(ShopMember(chest, "fridge"))) return false;
        object? special = ShopMember(chest, "SpecialChestType");
        return special != null && special.ToString() == "None";
    }

    private string RememberChest(string location, int x, int y, Chest chest, bool markObserved = false)
    {
        if (!_memoryLoaded) return "";
        if (!chest.modData.TryGetValue(ChestMemoryIdKey, out string? id) || string.IsNullOrWhiteSpace(id))
        {
            id = Guid.NewGuid().ToString("N");
            chest.modData[ChestMemoryIdKey] = id;
        }
        var items = chest.Items.Select((item, slot) => (item, slot)).Where(p => p.item != null)
            .Select(p => new ChestItemMemory
            {
                Slot = p.slot,
                ItemId = p.item.QualifiedItemId,
                Name = p.item.DisplayName,
                Quantity = p.item.Stack,
                Quality = TradeQuality(p.item),
                Category = p.item.Category
            }).ToList();
        ChestColorMemory color = ReadChestMemoryColor(chest);
        int capacity;
        try { capacity = ClosedStorageCapacity(chest); }
        catch { capacity = Math.Max(chest.Items.Count, 0); }
        ChestMemory? existing = _notebook.Chests.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        bool changed = existing == null || existing.Location != location || existing.TileX != x || existing.TileY != y
            || existing.Capacity != capacity || !SameColor(existing.Color, color) || !SameContents(existing.Contents, items);
        if (!changed && markObserved && (existing!.ObservedGameDate != FarmDate() || existing.ObservedGameTime != Game1.timeOfDay))
            changed = true;
        if (!changed) return id;
        ChestPurposeMemory purpose = existing?.Purpose ?? new();
        ChestMemory memory = new()
        {
            Id = id,
            Location = location,
            TileX = x,
            TileY = y,
            Color = color,
            Purpose = purpose,
            Capacity = capacity,
            Contents = items,
            ObservedGameDate = FarmDate(),
            ObservedGameTime = Game1.timeOfDay,
            ObservedAtUtc = DateTime.UtcNow.ToString("O")
        };
        if (existing == null) _notebook.Chests.Add(memory);
        else _notebook.Chests[_notebook.Chests.IndexOf(existing)] = memory;
        _notebookDirty = true;
        return id;
    }

    private void RefreshChestMemory(Chest chest)
    {
        if (!_memoryLoaded || !Context.IsWorldReady) return;
        foreach (var entry in Game1.currentLocation.Objects.Pairs)
            if (ReferenceEquals(entry.Value, chest))
            {
                RememberChest(Game1.currentLocation.Name, (int)entry.Key.X, (int)entry.Key.Y, chest);
                FlushLongTermMemory();
                return;
            }
    }

    private static ChestColorMemory ReadChestMemoryColor(Chest chest)
    {
        object? holder = ShopMember(chest, "playerChoiceColor");
        object? raw = holder == null ? null : ShopMember(holder, "Value") ?? holder;
        return raw is Color color
            ? new ChestColorMemory { R = color.R, G = color.G, B = color.B, A = color.A }
            : new ChestColorMemory();
    }

    private static bool SameColor(ChestColorMemory a, ChestColorMemory b) => a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;
    private static bool SameContents(IReadOnlyList<ChestItemMemory> a, IReadOnlyList<ChestItemMemory> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            ChestItemMemory x = a[i], y = b[i];
            if (x.Slot != y.Slot || x.ItemId != y.ItemId || x.Name != y.Name || x.Quantity != y.Quantity || x.Quality != y.Quality || x.Category != y.Category)
                return false;
        }
        return true;
    }

    private static PersistentTask CloneTask(PersistentTask task) => Clone(task);
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))
        ?? throw new InvalidOperationException("Could not clone persistent memory data.");
}
