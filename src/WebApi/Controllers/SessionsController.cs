using System.Collections.ObjectModel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using OpenSpace.Application.Entities;
using OpenSpace.Application.Exceptions;
using OpenSpace.Application.Repositories;
using OpenSpace.Application.Services;
using OpenSpace.WebApi.Hubs;

namespace OpenSpace.WebApi.Controllers;

[Route("api/sessions")]
public class SessionsController : Controller
{
    private readonly ICalendarService _calendarService;
    private readonly ISessionRepository _sessionRepository;
    private readonly IHubContext<SessionsHub, ISessionsHub> _sessionsHub;

    public SessionsController(ISessionRepository sessionRepository, ICalendarService calendarService, IHubContext<SessionsHub, ISessionsHub> sessionsHub)
    {
        _sessionRepository = sessionRepository;
        _calendarService = calendarService;
        _sessionsHub = sessionsHub;
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteSessionAsync(int id)
    {
        var success = await _sessionRepository.Delete(id);
        if (!success)
        {
            return NotFound();
        }

        await _sessionsHub.Clients.Group(id.ToString()).DeleteSession();
        return Ok();
    }

    [HttpGet]
    public Task<IEnumerable<Session>> GetSessionsAsync() => _sessionRepository.Get();

    [HttpGet("{id}")]
    public Task<Session> GetSessionByIdAsync(int id) => _sessionRepository.Get(id);

    [HttpGet("last")]
    public async Task<IEnumerable<Session>> GetLastSessionsAsync() => (await _sessionRepository.Get()).OrderByDescending(s => s.Id).Take(10);

    [HttpGet("{id}/calendar")]
    public async Task<IActionResult> GetSessionCalendarAsync(int id)
    {
        var calendar = await _calendarService.GetSessionsAsync(id);

        Response.Headers["Content-Disposition"] = "attachment; filename=\"Session" + id + ".ics\"";
        return Content(calendar, "text/calendar");
    }

    [HttpGet("calendar")]
    public async Task<IActionResult> GetSessionsCalendarAsync()
    {
        var calendar = await _calendarService.GetSessionsAsync();

        Response.Headers["Content-Disposition"] = "attachment; filename=\"Sessions.ics\"";
        return Content(calendar, "text/calendar");
    }

    [HttpPost]
    public Task<Session> AddSessionAsync() => _sessionRepository.Create();

    [HttpPut("{id}")]
    public async Task<Session> UpdateSessionAsync(int id, [FromBody] Session session)
    {
        await _sessionRepository.Update(session);
        await _sessionsHub.Clients.Group(id.ToString()).UpdateSession(session);

        return session;
    }

    [HttpDelete("{id}/attendances")]
    public Task ResetSessionAttendancesAsync(int id)
        => _sessionRepository.Update(id, session =>
        {
            foreach (var topic in session.Topics)
            {
                topic.Attendees.Clear();
                _sessionsHub.Clients.Group(id.ToString()).UpdateTopic(topic);
            }
        });

    [HttpDelete("{id}/ratings")]
    public Task ResetSessionRatingsAsync(int id)
        => _sessionRepository.Update(id, session =>
        {
            foreach (var topic in session.Topics)
            {
                topic.Ratings.Clear();
                _sessionsHub.Clients.Group(id.ToString()).UpdateTopic(topic);
            }
        });

    [HttpPut("{id}/optimise")]
    public Task OptimiseSessionTopicsAsync(int id, [FromBody] OptimiseTopicsConfigs configs)
        => _sessionRepository.Update(id, session =>
        {
            var rooms = session.Rooms.OrderByDescending(r => r.Seats).ToList();

            var slots = session.Slots.ToList();

            // #region Segeregating topic based on assignment
            var unassignedTopics = new List<Topic>();
            var assignedTopics = new List<Topic>();
            foreach (var topic in session.Topics)
            {
                if (string.IsNullOrEmpty(topic.SlotId) || string.IsNullOrEmpty(topic.RoomId))
                {
                    unassignedTopics.Add(topic);
                }
                else
                {
                    var topicSlot = session.Slots.FirstOrDefault(x => x.Id.Equals(topic.SlotId)) ?? throw new EntityNotFoundException("Slot not found");

                    var topicRoom = rooms.FirstOrDefault(r => r.Id.Equals(topic.RoomId)) ?? throw new EntityNotFoundException("Room not found");

                    if (topicSlot != null && topicRoom != null)
                    {
                        assignedTopics.Add(topic);
                    }
                }
            }

            unassignedTopics = unassignedTopics.OrderByDescending(t => t.Attendees.Count).ToList();

            // Order assigned topics on slots by attendees count
            assignedTopics = assignedTopics.OrderByDescending(t => t.Attendees.Count).ToList(); // #endregion

            Dictionary<(int, int), Topic> calendar = new();

            // #region  Assigned Topics Senario 1
            int roomId = 0, slotId = 0;
            for (var i = 0; i < assignedTopics.Count; i++)
            {
                var topicAssigned = false;
                var topic = assignedTopics[i];

                for (var s = slotId; s < slots.Count; s++)
                {
                    var slot = slots[s];

                    for (var r = roomId; r < rooms.Count; r++)
                    {
                        var room = rooms[r];

                        if (calendar.ContainsKey((s, r)))
                        {
                            continue;
                        }

                        if (topic.Attendees.Count > room.Seats.GetValueOrDefault())
                        {
                            s++;
                            break;
                        }

                        var newTopic = topic with
                        {
                            SlotId = slot.Id,
                            RoomId = room.Id,
                        };
                        session.Topics.Remove(topic);
                        session.Topics.Add(newTopic);
                        calendar[(s, r)] = newTopic;
                        topicAssigned = true;
                        _sessionsHub.Clients.Group(id.ToString()).UpdateTopic(newTopic);

                        break;
                    }

                    if (topicAssigned)
                    {
                        break;
                    }
                }

                if (!topicAssigned)
                {
                    var newTopic = topic with
                    {
                        SlotId = null,
                        RoomId = null,
                    };
                    session.Topics.Remove(topic);
                    session.Topics.Add(newTopic);
                    unassignedTopics.Add(newTopic);
                    _sessionsHub.Clients.Group(id.ToString()).UpdateTopic(newTopic);
                }
            }

            if (configs.OptimiseUnAssignedTopics)
            {
                roomId = 0;
                slotId = 0;
                for (var i = 0; i < unassignedTopics.Count; i++)
                {
                    var topicAssigned = false;
                    var topic = unassignedTopics[i];

                    for (var s = slotId; s < slots.Count; s++)
                    {
                        var slot = slots[s];

                        for (var r = roomId; r < rooms.Count; r++)
                        {
                            var room = rooms[r];

                            if (calendar.ContainsKey((s, r)))
                            {
                                continue;
                            }

                            if (topic.Attendees.Count > room.Seats.GetValueOrDefault())
                            {
                                s++;
                                break;
                            }

                            var newTopic = topic with
                            {
                                SlotId = slot.Id,
                                RoomId = room.Id,
                            };
                            session.Topics.Remove(topic);
                            session.Topics.Add(newTopic);
                            calendar[(s, r)] = newTopic;
                            topicAssigned = true;
                            _sessionsHub.Clients.Group(id.ToString()).UpdateTopic(newTopic);
                            break;
                        }

                        if (topicAssigned)
                        {
                            break;
                        }
                    }
                }
            }

            if (!configs.RectifyConflicts)
            {
                return;
            }

            Dictionary<(string, string), List<Topic>> ownerTopics = new();
            foreach (var topic in session.Topics)
            {
                if (string.IsNullOrEmpty(topic.SlotId) || string.IsNullOrEmpty(topic.RoomId) || string.IsNullOrEmpty(topic.Owner))
                {
                    continue;
                }

                if (ownerTopics.ContainsKey((topic.Owner!, topic.SlotId!)))
                {
                    ownerTopics[(topic.Owner!, topic.SlotId!)].Add(topic);
                }
                else
                {
                    ownerTopics[(topic.Owner!, topic.SlotId!)] = new List<Topic>() { topic };
                }
            }

            foreach (var ownerTopic in ownerTopics.ToDictionary(x => x.Key, y => y.Value))
            {
                var slotid = ownerTopic.Key.Item2;
                var owner = ownerTopic.Key.Item1;
                if (ownerTopic.Value.Count > 1)
                {
                    for (var i = 1; i < ownerTopic.Value.Count; i++)
                    {
                        var newPositionAssigned = false;
                        var topic = ownerTopic.Value[i];
                        for (var s = 0; s < slots.Count; s++)
                        {
                            var slot = slots[s];

                            if (slot.Id.Equals(slotid))
                            {
                                continue;
                            }

                            if (ownerTopics.ContainsKey((owner, slot.Id)))
                            {
                                continue;
                            }

                            for (var r = 0; r < rooms.Count; r++)
                            {
                                var room = rooms[r];

                                if (!calendar.ContainsKey((s, r)))
                                {
                                    if (room.Seats >= topic.Attendees.Count)
                                    {
                                        var newTopic = topic with
                                        {
                                            RoomId = room.Id,
                                            SlotId = slot.Id,
                                        };
                                        session.Topics.Remove(topic);
                                        session.Topics.Add(newTopic);
                                        calendar[(r, s)] = newTopic;
                                        _sessionsHub.Clients.Group(id.ToString()).UpdateTopic(newTopic);

                                        ownerTopics[(owner, newTopic.SlotId!)] = new List<Topic>() { newTopic };
                                        newPositionAssigned = true;
                                    }
                                }
                                else
                                {
                                    // checking
                                    var existingTopic = calendar[(s, r)];
                                    var existingTopicPotentialRoom = rooms.FirstOrDefault(x => x.Id.Equals(topic.RoomId));
                                    var existingTopicPotentialSlot = slots.FirstOrDefault(x => x.Id.Equals(topic.SlotId));
                                    if (!string.IsNullOrEmpty(existingTopic.Owner) && ownerTopics.ContainsKey((existingTopic.Owner!, existingTopicPotentialSlot?.Id!)))
                                    {
                                        continue;
                                    }

                                    if (room.Seats >= topic.Attendees.Count && existingTopic.Attendees.Count <= existingTopicPotentialRoom?.Seats.GetValueOrDefault())
                                    {
                                        var newTopic = topic with
                                        {
                                            RoomId = room.Id,
                                            SlotId = slot.Id,
                                        };
                                        session.Topics.Remove(topic);
                                        session.Topics.Add(newTopic);
                                        calendar[(r, s)] = newTopic;
                                        _sessionsHub.Clients.Group(id.ToString()).UpdateTopic(newTopic);
                                        ownerTopics[(owner, newTopic.SlotId!)] = new List<Topic>() { newTopic };

                                        var replacedTopic = existingTopic with
                                        {
                                            RoomId = existingTopicPotentialRoom.Id,
                                            SlotId = existingTopicPotentialSlot?.Id,
                                        };
                                        session.Topics.Remove(existingTopic);
                                        session.Topics.Add(replacedTopic);
                                        _sessionsHub.Clients.Group(id.ToString()).UpdateTopic(replacedTopic);

                                        ownerTopics[(existingTopic.Owner!, existingTopicPotentialSlot?.Id!)] = new List<Topic>() { replacedTopic };

                                        if (ownerTopics.ContainsKey((existingTopic.Owner!, slot.Id)))
                                        {
                                            var oldslot = ownerTopics[(existingTopic.Owner!, slot.Id)];
                                            if (oldslot.Count > 1)
                                            {
                                                oldslot.Remove(existingTopic);
                                            }
                                            else
                                            {
                                                ownerTopics.Remove((existingTopic.Owner!, slot.Id));
                                            }
                                        }

                                        newPositionAssigned = true;
                                    }
                                }

                                if (newPositionAssigned)
                                {
                                    break;
                                }
                            }

                            if (newPositionAssigned)
                            {
                                break;
                            }
                        }
                    }
                }
            }
        });
}
