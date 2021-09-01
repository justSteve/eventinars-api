using System.Data;
using System.Linq.Expressions;
using System.Text;
using AutoMapper;
using Dapper;
using DN.WebApi.Application.Abstractions.Repositories;
using DN.WebApi.Application.Abstractions.Services.General;
using DN.WebApi.Application.Constants;
using DN.WebApi.Application.Exceptions;
using DN.WebApi.Domain.Contracts;
using DN.WebApi.Shared.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace DN.WebApi.Infrastructure.Persistence.Repositories
{
    public class RepositoryAsync : IRepositoryAsync
    {
        private readonly IStringLocalizer<RepositoryAsync> _localizer;
        private readonly IMapper _mapper;
        private readonly IDistributedCache _cache;
        private readonly ApplicationDbContext _dbContext;
        private readonly ILogger<RepositoryAsync> _logger;
        private ISerializerService _serializer;

        public RepositoryAsync(ApplicationDbContext dbContext, ISerializerService serializer, IDistributedCache cache, ILogger<RepositoryAsync> logger, IMapper mapper, IStringLocalizer<RepositoryAsync> localizer)
        {
            _dbContext = dbContext;
            _serializer = serializer;
            _cache = cache;
            _logger = logger;
            _mapper = mapper;
            _localizer = localizer;
        }

        #region  Entity Framework Core : Get All
        public async Task<List<T>> GetListAsync<T>(Expression<Func<T, bool>> expression, bool noTracking = false, CancellationToken cancellationToken = default)
        where T : BaseEntity
        {
            IQueryable<T> query = _dbContext.Set<T>();
            if (noTracking) query = query.AsNoTracking();
            if (expression != null) query = query.Where(expression);
            return await query.ToListAsync(cancellationToken);
        }
        #endregion
        public async Task<T> GetByIdAsync<T>(object entityId, CancellationToken cancellationToken = default)
        where T : BaseEntity
        {
            return await _dbContext.Set<T>().FindAsync(entityId);
        }

        public async Task<TDto> GetCachedDtoByIdAsync<T, TDto>(object entityId, CancellationToken cancellationToken = default)
        where T : BaseEntity
        where TDto : IDto
        {
            var cacheKey = CacheKeys.GetCacheKey<T>(entityId);
            byte[] cachedData = !string.IsNullOrWhiteSpace(cacheKey) ? await _cache.GetAsync(cacheKey, cancellationToken) : null;
            if (cachedData != null)
            {
                await _cache.RefreshAsync(cacheKey);
                _logger.LogInformation(string.Format(_localizer["caching.refreshed"], cacheKey));
                var entity = _serializer.Deserialize<TDto>(Encoding.Default.GetString(cachedData));
                return entity;
            }
            else
            {
                var entity = await _dbContext.Set<T>().FindAsync(entityId);
                var dto = _mapper.Map<TDto>(entity);
                if (dto != null)
                {
                    var options = new DistributedCacheEntryOptions();
                    byte[] serializedData = Encoding.Default.GetBytes(_serializer.Serialize(dto));
                    await _cache.SetAsync(cacheKey, serializedData, options, cancellationToken);
                    _logger.LogInformation(string.Format(_localizer["caching.added"], cacheKey));
                    return dto;
                }

                throw new EntityNotFoundException(string.Format(_localizer["entity.notfound"], typeof(T).Name, entityId));
            }
        }

        public async Task<List<T>> GetPaginatedListAsync<T>(int pageNumber, int pageSize)
        where T : BaseEntity
        {
            return await _dbContext
                .Set<T>()
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .AsNoTracking()
                .ToListAsync();
        }

        public async Task<object> CreateAsync<T>(T entity)
        where T : BaseEntity
        {
            await _dbContext.Set<T>().AddAsync(entity);
            return entity.Id;
        }

        public async Task<bool> ExistsAsync<T>(Expression<Func<T, bool>> expression, CancellationToken cancellationToken = default)
        where T : BaseEntity
        {
            IQueryable<T> query = _dbContext.Set<T>();
            if (expression != null) return await query.AnyAsync(expression, cancellationToken);
            return await query.AnyAsync(cancellationToken);
        }

        public Task RemoveAsync<T>(T entity)
        where T : BaseEntity
        {
            var cacheKey = CacheKeys.GetCacheKey<T>(entity.Id);
            _dbContext.Set<T>().Remove(entity);
            _cache.Remove(cacheKey);
            return Task.CompletedTask;
        }

        public Task UpdateAsync<T>(T entity)
        where T : BaseEntity
        {
            T exist = _dbContext.Set<T>().Find(entity.Id);
            _dbContext.Entry(exist).CurrentValues.SetValues(entity);
            return Task.CompletedTask;
        }

        #region Dapper
        public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object param = null, IDbTransaction transaction = null, CancellationToken cancellationToken = default)
        where T : BaseEntity
        {
            return (await _dbContext.Connection.QueryAsync<T>(sql, param, transaction)).AsList();
        }

        public async Task<T> QueryFirstOrDefaultAsync<T>(string sql, object param = null, IDbTransaction transaction = null, CancellationToken cancellationToken = default)
        where T : BaseEntity
        {
            // Dapper isn't advanced enough to support MultiTenancy
            // Workaround - In Repository Layer, I check if T implements IMustHaveTenant Interface. If so, replaces @tenantId with currentTenantId in the SQL query.
            // Not a clean way, but works.
            // Make sure to include TenantId='@tenantId' in your queries.
            if (typeof(IMustHaveTenant).IsAssignableFrom(typeof(T)))
            {
                sql = sql.Replace("@tenantId", _dbContext.TenantId);
            }

            var entity = await _dbContext.Connection.QueryFirstOrDefaultAsync<T>(sql, param, transaction);
            if (entity == null) throw new EntityNotFoundException(string.Empty);
            return entity;
        }

        public async Task<T> QuerySingleAsync<T>(string sql, object param = null, IDbTransaction transaction = null, CancellationToken cancellationToken = default)
        where T : BaseEntity
        {
            if (typeof(IMustHaveTenant).IsAssignableFrom(typeof(T)))
            {
                sql = sql.Replace("@tenantId", _dbContext.TenantId);
            }

            return await _dbContext.Connection.QuerySingleAsync<T>(sql, param, transaction);
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return _dbContext.SaveChangesAsync(cancellationToken);
        }
        #endregion
    }
}