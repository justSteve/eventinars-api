using System.Text;
using System.Text.Encodings.Web;
using DN.WebApi.Application.Abstractions.Services.General;
using DN.WebApi.Application.Abstractions.Services.Identity;
using DN.WebApi.Application.Exceptions;
using DN.WebApi.Application.Settings;
using DN.WebApi.Application.Wrapper;
using DN.WebApi.Infrastructure.Identity.Models;
using DN.WebApi.Shared.DTOs.General.Requests;
using DN.WebApi.Shared.DTOs.Identity.Requests;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace DN.WebApi.Infrastructure.Identity.Services
{
    public class IdentityService : IIdentityService
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IJobService _jobService;
        private readonly IMailService _mailService;
        private readonly MailSettings _mailSettings;
        private readonly IStringLocalizer<IdentityService> _localizer;
        private readonly ITenantService _tenantService;

        public IdentityService(
            UserManager<ApplicationUser> userManager,
            IJobService jobService,
            IMailService mailService,
            IOptions<MailSettings> mailSettings,
            IStringLocalizer<IdentityService> localizer,
            ITenantService tenantService)
        {
            _userManager = userManager;
            _jobService = jobService;
            _mailService = mailService;
            _mailSettings = mailSettings.Value;
            _localizer = localizer;
            _tenantService = tenantService;
        }

        public async Task<IResult> RegisterAsync(RegisterRequest request, string origin)
        {
            var users = await _userManager.Users.ToListAsync();
            var userWithSameUserName = await _userManager.FindByNameAsync(request.UserName);
            if (userWithSameUserName != null)
            {
                throw new IdentityException(string.Format(_localizer["Username {0} is already taken."], request.UserName));
            }

            var user = new ApplicationUser
            {
                Email = request.Email,
                FirstName = request.FirstName,
                LastName = request.LastName,
                UserName = request.UserName,
                PhoneNumber = request.PhoneNumber,
                IsActive = true,
                TenantId = _tenantService.GetTenant()?.TID
            };
            if (!string.IsNullOrWhiteSpace(request.PhoneNumber))
            {
                var userWithSamePhoneNumber = await _userManager.Users.FirstOrDefaultAsync(x => x.PhoneNumber == request.PhoneNumber);
                if (userWithSamePhoneNumber != null)
                {
                    throw new IdentityException(string.Format(_localizer["Phone number {0} is already registered."], request.PhoneNumber));
                }
            }

            var userWithSameEmail = await _userManager.FindByEmailAsync(request.Email);
            if (userWithSameEmail == null)
            {
                var result = await _userManager.CreateAsync(user, request.Password);
                if (result.Succeeded)
                {
                    var messages = new List<string> { string.Format(_localizer["User {0} Registered."], user.UserName) };
                    if (_mailSettings.EnableVerification)
                    {
                        // send verification email
                        string emailVerificationUri = await GetEmailVerificationUriAsync(user, origin);
                        var mailRequest = new MailRequest
                        {
                            From = _mailSettings.From,
                            To = user.Email,
                            Body = string.Format(_localizer["Please confirm your account by <a href='{0}'>clicking here</a>."], emailVerificationUri),
                            Subject = _localizer["Confirm Registration"]
                        };
                        _jobService.Enqueue(() => _mailService.SendAsync(mailRequest));
                        messages.Add(_localizer[$"Please check {user.Email} to verify your account!"]);
                    }

                    return await Result<string>.SuccessAsync(user.Id, messages: messages);
                }
                else
                {
                    throw new IdentityException(_localizer["Validation Errors Occurred."], result.Errors.Select(a => _localizer[a.Description].ToString()).ToList());
                }
            }
            else
            {
                throw new IdentityException(string.Format(_localizer["Email {0} is already registered."], request.Email));
            }
        }

        private async Task<string> GetEmailVerificationUriAsync(ApplicationUser user, string origin)
        {
            string code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
            string route = "api/identity/confirm-email/";
            var endpointUri = new Uri(string.Concat($"{origin}/", route));
            string verificationUri = QueryHelpers.AddQueryString(endpointUri.ToString(), "userId", user.Id);
            verificationUri = QueryHelpers.AddQueryString(verificationUri, "code", code);
            return verificationUri;
        }

        private async Task<string> GetMobilePhoneVerificationCodeAsync(ApplicationUser user)
        {
            return await _userManager.GenerateChangePhoneNumberTokenAsync(user, user.PhoneNumber);
        }

        public async Task<IResult<string>> ConfirmEmailAsync(string userId, string code)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                throw new IdentityException(_localizer["An error occurred while confirming E-Mail."]);
            }

            code = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
            var result = await _userManager.ConfirmEmailAsync(user, code);
            if (result.Succeeded)
            {
                return await Result<string>.SuccessAsync(user.Id, string.Format(_localizer["Account Confirmed for E-Mail {0}. You can now use the /api/identity/token endpoint to generate JWT."], user.Email));
            }
            else
            {
                throw new IdentityException(string.Format(_localizer["An error occurred while confirming {0}"], user.Email));
            }
        }

        public async Task<IResult<string>> ConfirmPhoneNumberAsync(string userId, string code)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                throw new IdentityException(_localizer["An error occurred while confirming Mobile Phone."]);
            }

            var result = await _userManager.ChangePhoneNumberAsync(user, user.PhoneNumber, code);
            if (result.Succeeded)
            {
                if (user.EmailConfirmed)
                {
                    return await Result<string>.SuccessAsync(user.Id, string.Format(_localizer["Account Confirmed for Phone Number {0}. You can now use the /api/identity/token endpoint to generate JWT."], user.PhoneNumber));
                }
                else
                {
                    return await Result<string>.SuccessAsync(user.Id, string.Format(_localizer["Account Confirmed for Phone Number {0}. You should confirm your E-mail before using the /api/identity/token endpoint to generate JWT."], user.PhoneNumber));
                }
            }
            else
            {
                throw new IdentityException(string.Format(_localizer["An error occurred while confirming {0}"], user.PhoneNumber));
            }
        }

        public async Task<IResult> ForgotPasswordAsync(ForgotPasswordRequest request, string origin)
        {
            var user = await _userManager.FindByEmailAsync(request.Email);
            if (user == null || !(await _userManager.IsEmailConfirmedAsync(user)))
            {
                // Don't reveal that the user does not exist or is not confirmed
                throw new IdentityException(_localizer["An Error has occurred!"]);
            }

            // For more information on how to enable account confirmation and password reset please
            // visit https://go.microsoft.com/fwlink/?LinkID=532713
            string code = await _userManager.GeneratePasswordResetTokenAsync(user);
            code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
            string route = "account/reset-password";
            var endpointUri = new Uri(string.Concat($"{origin}/", route));
            string passwordResetUrl = QueryHelpers.AddQueryString(endpointUri.ToString(), "Token", code);
            var mailRequest = new MailRequest
            {
                Body = string.Format(_localizer["Please reset your password by <a href='{0}>clicking here</a>."], HtmlEncoder.Default.Encode(passwordResetUrl)),
                Subject = _localizer["Reset Password"],
                To = request.Email
            };
            _jobService.Enqueue(() => _mailService.SendAsync(mailRequest));
            return await Result.SuccessAsync(_localizer["Password Reset Mail has been sent to your authorized Email."]);
        }

        public async Task<IResult> ResetPasswordAsync(ResetPasswordRequest request)
        {
            var user = await _userManager.FindByEmailAsync(request.Email);
            if (user == null)
            {
                // Don't reveal that the user does not exist
                throw new IdentityException(_localizer["An Error has occurred!"]);
            }

            var result = await _userManager.ResetPasswordAsync(user, request.Token, request.Password);
            if (result.Succeeded)
            {
                return await Result.SuccessAsync(_localizer["Password Reset Successful!"]);
            }
            else
            {
                throw new IdentityException(_localizer["An Error has occurred!"]);
            }
        }
    }
}