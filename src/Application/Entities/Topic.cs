namespace OpenSpace.Application.Entities;

public record Topic(
    string Id,
    string Name,
    string? Description,
    string? Owner,
    string? RoomId,
    string? SlotId,
    ICollection<Attendance> Attendees,
    ICollection<string> Demands,
    ICollection<Feedback> Feedback,
    ICollection<Rating> Ratings,
    int Slots = 1);

public record OptimiseTopicsConfigs(bool OptimiseUnAssignedTopics = false, bool RectifyConflicts = true);
