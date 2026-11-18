/*
    ПР5 «Менеджер лицензий» — база данных сервера.
    СУБД: Microsoft SQL Server (2017+ / Express / LocalDB).

    Пароли хранятся как SHA-256 от строки (Salt + Password) в кодировке UTF-16LE
    (именно так считает HASHBYTES для NVARCHAR и Encoding.Unicode в C#).
*/

IF DB_ID(N'LicenseManager') IS NULL
    CREATE DATABASE LicenseManager;
GO

USE LicenseManager;
GO

IF OBJECT_ID(N'dbo.ConnectionLog', N'U') IS NOT NULL DROP TABLE dbo.ConnectionLog;
IF OBJECT_ID(N'dbo.Blacklist',     N'U') IS NOT NULL DROP TABLE dbo.Blacklist;
IF OBJECT_ID(N'dbo.Users',         N'U') IS NOT NULL DROP TABLE dbo.Users;
GO

/* Пользователи, которым разрешено получать лицензию */
CREATE TABLE dbo.Users
(
    Id           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
    Login        NVARCHAR(50)      NOT NULL CONSTRAINT UQ_Users_Login UNIQUE,
    PasswordHash VARBINARY(32)     NOT NULL,
    Salt         NVARCHAR(32)      NOT NULL,
    FullName     NVARCHAR(100)     NULL,
    CreatedAt    DATETIME2(0)      NOT NULL CONSTRAINT DF_Users_CreatedAt DEFAULT (SYSDATETIME())
);
GO

/* Чёрный список: блокировка по логину и/или по IP-адресу */
CREATE TABLE dbo.Blacklist
(
    Id        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Blacklist PRIMARY KEY,
    Login     NVARCHAR(50)      NULL,
    IpAddress NVARCHAR(45)      NULL,
    Reason    NVARCHAR(200)     NULL,
    AddedAt   DATETIME2(0)      NOT NULL CONSTRAINT DF_Blacklist_AddedAt DEFAULT (SYSDATETIME()),
    CONSTRAINT CK_Blacklist_Target CHECK (Login IS NOT NULL OR IpAddress IS NOT NULL)
);
GO

/* Журнал подключений (кто, откуда, какой код, сколько был подключён) */
CREATE TABLE dbo.ConnectionLog
(
    Id               INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConnectionLog PRIMARY KEY,
    UserId           INT               NULL CONSTRAINT FK_ConnectionLog_Users REFERENCES dbo.Users(Id) ON DELETE SET NULL,
    Login            NVARCHAR(50)      NULL,
    IpAddress        NVARCHAR(45)      NOT NULL,
    Port             INT               NOT NULL,
    LicenseCode      NVARCHAR(32)      NULL,
    ConnectedAt      DATETIME2(0)      NOT NULL,
    DisconnectedAt   DATETIME2(0)      NULL,
    DisconnectReason NVARCHAR(50)      NULL
);
GO

/* Тестовые пользователи (пароль у всех: 12345) */
INSERT INTO dbo.Users (Login, PasswordHash, Salt, FullName)
VALUES
    (N'damir',  HASHBYTES('SHA2_256', N'a1f3c9e2' + N'12345'), N'a1f3c9e2', N'Ходжиев Дамир'),
    (N'client', HASHBYTES('SHA2_256', N'7b0d44aa' + N'12345'), N'7b0d44aa', N'Тестовый клиент'),
    (N'user1',  HASHBYTES('SHA2_256', N'c3e8512f' + N'12345'), N'c3e8512f', N'Пользователь 1'),
    (N'user2',  HASHBYTES('SHA2_256', N'9f2a6b10' + N'12345'), N'9f2a6b10', N'Пользователь 2'),
    (N'banned', HASHBYTES('SHA2_256', N'5e7c0d93' + N'12345'), N'5e7c0d93', N'Заблокированный пользователь');
GO

/* Пример записи в чёрном списке */
INSERT INTO dbo.Blacklist (Login, IpAddress, Reason)
VALUES (N'banned', NULL, N'Тестовая блокировка');
GO
