﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Authorization;
using Volo.Abp.Data;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Features;
using Volo.Abp.GlobalFeatures;
using Volo.Abp.Users;
using Volo.CmsKit.Comments;
using Volo.CmsKit.Features;
using Volo.CmsKit.GlobalFeatures;
using Volo.CmsKit.Permissions;
using Volo.CmsKit.Users;

namespace Volo.CmsKit.Public.Comments;

[RequiresFeature(CmsKitFeatures.CommentEnable)]
[RequiresGlobalFeature(typeof(CommentsFeature))]
public class CommentPublicAppService : CmsKitPublicAppServiceBase, ICommentPublicAppService
{
    protected string RegexMarkdownUrlPattern = @"\[[^\]]*\]\((?<url>.*?)\)(?![^\x60]*\x60)";
    
    protected ICommentRepository CommentRepository { get; }
    protected ICmsUserLookupService CmsUserLookupService { get; }
    public IDistributedEventBus DistributedEventBus { get; }
    protected CommentManager CommentManager { get; }
    
    protected CmsKitCommentOptions CmsCommentOptions { get; }

    public CommentPublicAppService(
        ICommentRepository commentRepository,
        ICmsUserLookupService cmsUserLookupService,
        IDistributedEventBus distributedEventBus,
        CommentManager commentManager,
        IOptionsSnapshot<CmsKitCommentOptions> cmsCommentOptions)
    {
        CommentRepository = commentRepository;
        CmsUserLookupService = cmsUserLookupService;
        DistributedEventBus = distributedEventBus;
        CommentManager = commentManager;
        CmsCommentOptions = cmsCommentOptions.Value;
    }

    public virtual async Task<ListResultDto<CommentWithDetailsDto>> GetListAsync(string entityType, string entityId)
    {
        var commentsWithAuthor = await CommentRepository
            .GetListWithAuthorsAsync(entityType, entityId);

        return new ListResultDto<CommentWithDetailsDto>(
            ConvertCommentsToNestedStructure(commentsWithAuthor)
        );
    }

    [Authorize]
    public virtual async Task<CommentDto> CreateAsync(string entityType, string entityId, CreateCommentInput input)
    {
        CheckExternalUrls(entityType, input.Text);
        
        var user = await CmsUserLookupService.GetByIdAsync(CurrentUser.GetId());

        if (input.RepliedCommentId.HasValue)
        {
            await CommentRepository.GetAsync(input.RepliedCommentId.Value);
        }

        var comment = await CommentRepository.InsertAsync(
            await CommentManager.CreateAsync(
                user,
                entityType,
                entityId,
                input.Text,
                input.RepliedCommentId
            )
        );

        await UnitOfWorkManager.Current.SaveChangesAsync();

        await DistributedEventBus.PublishAsync(new CreatedCommentEvent
        {
            Id = comment.Id
        });

        return ObjectMapper.Map<Comment, CommentDto>(comment);
    }

    [Authorize]
    public virtual async Task<CommentDto> UpdateAsync(Guid id, UpdateCommentInput input)
    {
        var comment = await CommentRepository.GetAsync(id);
        if (comment.CreatorId != CurrentUser.GetId())
        {
            throw new AbpAuthorizationException();
        }
        
        CheckExternalUrls(comment.EntityType, input.Text);

        comment.SetText(input.Text);
        comment.SetConcurrencyStampIfNotNull(input.ConcurrencyStamp);

        var updatedComment = await CommentRepository.UpdateAsync(comment);

        return ObjectMapper.Map<Comment, CommentDto>(updatedComment);
    }

    [Authorize]
    public virtual async Task DeleteAsync(Guid id)
    {
        var allowDelete = await AuthorizationService.IsGrantedAsync(CmsKitPublicPermissions.Comments.DeleteAll);

        var comment = await CommentRepository.GetAsync(id);
        if (allowDelete || comment.CreatorId == CurrentUser.Id)
        {
            await CommentRepository.DeleteWithRepliesAsync(comment);
        }
        else
        {
            throw new AbpAuthorizationException();
        }
    }

    private List<CommentWithDetailsDto> ConvertCommentsToNestedStructure(List<CommentWithAuthorQueryResultItem> comments)
    {
        //TODO: I think this method can be optimized if you use dictionaries instead of straight search

        var parentComments = comments
            .Where(c => c.Comment.RepliedCommentId == null)
            .Select(c => ObjectMapper.Map<Comment, CommentWithDetailsDto>(c.Comment))
            .ToList();

        foreach (var parentComment in parentComments)
        {
            parentComment.Author = GetAuthorAsDtoFromCommentList(comments, parentComment.Id);

            parentComment.Replies = comments
                .Where(c => c.Comment.RepliedCommentId == parentComment.Id)
                .Select(c => ObjectMapper.Map<Comment, CommentDto>(c.Comment))
                .ToList();

            foreach (var reply in parentComment.Replies)
            {
                reply.Author = GetAuthorAsDtoFromCommentList(comments, reply.Id);
            }
        }

        return parentComments;
    }

    private CmsUserDto GetAuthorAsDtoFromCommentList(List<CommentWithAuthorQueryResultItem> comments, Guid commentId)
    {
        return ObjectMapper.Map<CmsUser, CmsUserDto>(comments.Single(c => c.Comment.Id == commentId).Author);
    }

    private void CheckExternalUrls(string entityType, string text)
    {
        if (!CmsCommentOptions.AllowedExternalUrls.TryGetValue(entityType, out var allowedExternalUrls))
        {
            return;
        }

        var matches = Regex.Matches(text, RegexMarkdownUrlPattern,
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            if (!match.Success || match.Groups.Count < 2)
            {
                continue;
            }

            var url = NormalizeUrl(match.Groups[1].Value);
            if (!IsExternalUrl(url))
            {
                continue;
            }

            if (!allowedExternalUrls.Any(allowedExternalUrl =>
                    url.Contains(NormalizeUrl(allowedExternalUrl), StringComparison.OrdinalIgnoreCase)))
            {
                throw new UserFriendlyException(L["UnAllowedExternalUrlMessage"]);
            }
        }
    }

    private static bool IsExternalUrl(string url)
    {
        return url.StartsWith("https", StringComparison.InvariantCultureIgnoreCase) ||
               url.StartsWith("http", StringComparison.InvariantCultureIgnoreCase);
    }

    private static string NormalizeUrl(string url)
    {
        return url.Replace("www.", "").RemovePostFix("/");
    }
}
