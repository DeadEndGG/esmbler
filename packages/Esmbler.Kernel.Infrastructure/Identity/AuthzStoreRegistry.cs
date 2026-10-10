using Esmbler.Kernel.Authorization;
using Esmbler.Kernel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Esmbler.Kernel.Infrastructure.Identity;

public sealed class AuthzStoreRegistry(Database database) : IAuthzStoreRegistry
{
    public Task<(string StoreId, string ModelId)?> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        database.InTenantWideAsync<(string, string)?>(tenantId, null, async db =>
        {
            var row = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
            return row?.AuthzStoreId is { } s && row.AuthzModelId is { } m ? (s, m) : null;
        }, cancellationToken);

    public Task SaveAsync(Guid tenantId, string storeId, string modelId, CancellationToken cancellationToken) =>
        database.InTenantWideAsync(tenantId, null, async db =>
        {
            await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.AuthzStoreId, storeId).SetProperty(t => t.AuthzModelId, modelId), cancellationToken);
            return true;
        }, cancellationToken);
}
