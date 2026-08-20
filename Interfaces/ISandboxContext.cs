using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileAccessMcpServer.Interfaces
{
    /// <summary>
    /// Предоставляет доступ к настройкам песочницы: корневой каталог и каталог резервных копий.
    /// Значения определяются серверной конфигурацией (config.json, секция FileAccess).
    /// </summary>
    internal interface ISandboxContext
    {
        /// <summary>
        /// Возвращает корневой каталог песочницы из конфигурации (SandboxDir).
        /// </summary>
        string? SandboxDir { get; }

        /// <summary>
        /// Возвращает каталог для резервных копий из конфигурации (BackupDir).
        /// </summary>
        string? BackupDir { get; }
    }
}