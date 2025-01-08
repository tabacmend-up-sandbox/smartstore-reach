﻿using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Smartstore.Core.Identity;
using Smartstore.Core.Web;

namespace Smartstore.Core.Security
{
    public class OverloadProtector : IOverloadProtector
    {
        private readonly Work<ResiliencySettings> _settings;
        private readonly TrafficRateLimiters _rateLimiters;

        public OverloadProtector(
            Work<ResiliencySettings> settings, 
            TrafficRateLimiters rateLimiters,
            ILoggerFactory loggerFactory)
        {
            _settings = settings;
            _rateLimiters = rateLimiters;

            Logger = loggerFactory.CreateLogger("File/App_Data/Logs/overloadprotector-.log");
        }

        public ILogger Logger { get; }

        public virtual Task<bool> DenyGuestAsync(Customer customer = null)
            => Task.FromResult(CheckDeny(UserType.Guest));

        public virtual Task<bool> DenyBotAsync(IUserAgent userAgent)
            => Task.FromResult(CheckDeny(UserType.Bot));

        public virtual Task<bool> ForbidNewGuestAsync(HttpContext httpContext)
        {
            var forbid = _settings.Value.EnableOverloadProtection && _settings.Value.ForbidNewGuestsIfSubRequest && httpContext != null;
            if (forbid)
            {
                forbid = httpContext.Request.IsSubRequest();
                if (forbid)
                {
                    Logger.Warn("New guest forbidden due to policy (ForbidNewGuestsIfSubRequest).");
                }
            }
            
            return Task.FromResult(forbid);
        }

        private bool CheckDeny(UserType userType)
        {
            if (!_settings.Value.EnableOverloadProtection)
            {
                // Allowed, because protection is turned off.
                return false;
            }

            // Check both global and type-specific limits for peak usage
            var peakAllowed = TryAcquireFromGlobal(peak: true) && TryAcquireFromType(userType, peak: true);
            if (!peakAllowed)
            {
                // Deny the request if either limit fails
                return true;
            }

            // Check both global and type-specific limits for long usage
            var longAllowed = TryAcquireFromGlobal(peak: false) && TryAcquireFromType(userType, peak: false);
            if (!longAllowed)
            {
                // Deny the request if either limit fails
                return true;
            }

            // If we got here, either type or global allowed it
            return false; // no deny (allowed)
        }

        private bool TryAcquireFromType(UserType userType, bool peak)
        {
            var limiter = GetTypeLimiter(userType, peak);
            if (limiter != null)
            {
                using var lease = limiter.AttemptAcquire(1);

                if (!lease.IsAcquired)
                {
                    Logger.Warn("Rate limit exceeded. UserType: {0}, Peak: {1}", userType, peak);
                }

                return lease.IsAcquired;
            }

            // Always allow access if no rate limiting is configured
            return true;
        }

        private bool TryAcquireFromGlobal(bool peak)
        {
            var limiter = peak ? _rateLimiters.PeakGlobalLimiter : _rateLimiters.LongGlobalLimiter;
            if (limiter != null)
            {
                using var lease = limiter.AttemptAcquire(1);

                if (!lease.IsAcquired)
                {
                    Logger.Warn("Global rate limit exceeded. Peak: {0}", peak);
                }

                return lease.IsAcquired;
            }

            // Always allow access if no rate limiting is configured
            return true;
        }

        private RateLimiter GetTypeLimiter(UserType userType, bool peak)
        {
            return userType switch
            {
                UserType.Guest  => peak ? _rateLimiters.PeakGuestLimiter : _rateLimiters.LongGuestLimiter,
                _               => peak ? _rateLimiters.PeakBotLimiter : _rateLimiters.LongBotLimiter
            };
        }

        enum UserType
        {
            Guest,
            Bot
        }
    }
}
