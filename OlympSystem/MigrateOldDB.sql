set identity_insert [Compilators] on
insert into [Compilators] ([Id], [Name], [Language], [SourceExtension], [CommandLine], [ConfigName], [IsActive])
select [CompilatorId], [Name], [Language], [SourceExtension], [CommandLine], [ConfigName], [IsActive]
from [CALC].[Olymp5].[dbo].[Compilators]
set identity_insert [Compilators] off

set identity_insert [BinaryDatas] on
insert into [BinaryDatas] ([Id], [Data])
select [Id], [Data]
from [CALC].[Olymp5].[dbo].[BinaryDatas]
set identity_insert [BinaryDatas] off

set identity_insert [Schools] on
insert into [Schools] ([Id], [Name], [FullName], [DefaultTeamName])
select [OrganizationId], [Name], [FullName], [ACMName]
from [CALC].[Olymp5].[dbo].[Organizations]
set identity_insert [Schools] off

set identity_insert [News] on
insert into [News] ([Id], [Title], [Text], [PublicationDate])
select [NewsId], [Title], [Text], [PublicationDate]
from [CALC].[Olymp5].[dbo].[News]
set identity_insert [News] off

set identity_insert [Contests] on
insert into [Contests] ([Id], [Name], [Description], [Type], [Abbr], [Open], [Hidden], [StartTime], [EndTime], [TaskShowTime], [FreezeTime], [TaskPdfId], [TestZipId], [FinalTableId], [OfficialTableId])
select [OlympiadId], [Title], [Description], [Type], [Abbr], [Open], [Hidden], [StartTime], COALESCE([EndTime], [StartTime]), [TaskShowTime], [FreezeTime], [TaskPdfId], [TestZipId], [FinalTableId], [OfficialTableId]
from [CALC].[Olymp5].[dbo].[Olympiads]
set identity_insert [Contests] off

set identity_insert [Problems] on
insert into [Problems] ([Id], [Number], [Name], [Public], [Active], [OpenTestNumber], [TimeLimit], [SlowTimeLimit], [MemoryLimit], [Text], [ContestId])
select [TaskId], [Number], [Name], [Active], [Active], 
	(select count(*) from [CALC].[Olymp5].[dbo].[Tests] where [Tasks].[TaskId] = [Tests].[TaskId] and [Open] = 1),
	[TimeLimit], [SlowTimeLimit], [MemoryLimit], [Text], [OlympiadId]
from [CALC].[Olymp5].[dbo].[Tasks]
set identity_insert [Problems] off

set identity_insert [Tests] on
insert into [Tests] ([Id], [Number], [Score], [IsActive], [Comment], [Input], [Output], [ProblemId])
select [TestId], [Number], [Score], [Active], [Comment], [Input], [Output], [TaskId]
from [CALC].[Olymp5].[dbo].[Tests]
set identity_insert [Tests] off

set identity_insert [Users] on
insert into [Users] ([Id], [Login], [Password], [IsHidden], [Name], [Info], [RegistrationDate], [CoachId], [SchoolId], [ContestId], [IsApproved], [IsDisqualify], [IsNonOfficial])
select [UserId], [Login], [Password], [IsHidden], [Name], [Info],  [RegistrationDate], [CoachId], [OrganizationId], [OlympiadId], [IsApproved], [IsDisqualify], [IsNonOff]
from [CALC].[Olymp5].[dbo].[Users]
set identity_insert [Users] off

insert into [Participations] ([UserId], [UserDetailId])
select [UserId], [UserDetailId]
from [CALC].[Olymp5].[dbo].[Participations]

set identity_insert [Solutions] on
insert into [Solutions] ([Id], [UserId], [ProblemId], [CompilatorId], [CommitTime], [CommiterInfo], [SourceCode], [StatusCode], [Hidden], [Reference], [Description], [LastCheck])
select [SolutionId], [UserId], [TaskId], [CompilatorId], [CommitTime], [CommiterInfo], [SourceCode], [StatusCode], [Hidden], [Reference], [Description], [LastModification]
from [CALC].[Olymp5].[dbo].[Solutions]
set identity_insert [Solutions] off

set identity_insert [CheckLogs] on
insert into [CheckLogs] ([Id], [SolutionId], [CheckResultCode], [Log], [CheckTime])
select [CheckLogId], [SolutionId], [CheckResultCode], [Log], [CheckTime]
from [CALC].[Olymp5].[dbo].[CheckLogs]
set identity_insert [CheckLogs] off

set identity_insert [Clarifications] on
insert into [Clarifications] ([Id], [Text], [PublishTime], [ContestId], [ProblemId], [QuestionText], [QuestionTime], [UserId])
select [MessageId], [JuryText], [SendTime], [OlympiadId], [TaskId], [UserText], [SendTime], [UserId]
from [CALC].[Olymp5].[dbo].[Messages]
set identity_insert [Clarifications] off

insert into [AspNetUsers] ([Id], [DisplayName], [OlympUserId], [FirstName], [MiddleName], [LastName],
	[CreateDate], [LastLoginDate], [Year], [Detail], [Email], [EmailConfirmed],	[PhoneNumberConfirmed],
	[TwoFactorEnabled], [LockoutEnabled], [AccessFailedCount], [PasswordHash], [SecurityStamp], [UserName])
select NEWID(), [DisplayName], [Users].UserId, [FirstName], [MiddleName], [LastName],
	[yaf_User].[Joined], [LastVisit], [UserDetails].[Year], [UserDetails].[Detail], [yaf_User].[Email], 1, 0, 0, 0, 0,
	[yaf_prov_Membership].[Password] + '|1|' + [PasswordSalt], NEWID(), [yaf_User].[Name]
from [CALC].[Olymp5].[dbo].[yaf_User] 
join [CALC].[Olymp5].[dbo].[yaf_prov_Membership] on [yaf_User].[ProviderUserKey] = [yaf_prov_Membership].[UserID]
join [CALC].[Olymp5].[dbo].[Users] on [yaf_User].[Name] = [Users].[Login]
join [CALC].[Olymp5].[dbo].[UserDetails] on [Users].UserId = [UserDetails].UserId
