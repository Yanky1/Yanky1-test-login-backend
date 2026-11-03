namespace Solvelt.Api.Teams;

/// <summary>Extension point: other workstreams can veto removing a member (e.g. services: upcoming bookings assigned to them). Return a message to block, null to allow.</summary>
public interface IMemberRemovalGuard
{
    Task<string?> BlockReasonAsync(Guid sellerId, Guid membershipId);
}
