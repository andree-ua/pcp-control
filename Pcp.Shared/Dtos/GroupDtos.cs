namespace Pcp.Shared.Dtos;

/// <summary>
/// Grupo de pantallas para control masivo.
/// </summary>
public class GroupInfoDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>Identificadores de las pantallas asignadas al grupo.</summary>
    public List<string> ScreenIds { get; set; } = new();
}

/// <summary>
/// Datos para crear o actualizar un grupo y su membresía.
/// </summary>
public class GroupUpsertDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Lista completa de pantallas que pertenecen al grupo.</summary>
    public List<string> ScreenIds { get; set; } = new();
}

/// <summary>
/// Resultado de un comando enviado a un grupo.
/// </summary>
public class GroupCommandResponseDto
{
    public Guid GroupId { get; set; }

    public int Total { get; set; }

    public int Sent { get; set; }

    public int Offline { get; set; }

    public int Failed { get; set; }

    public List<GroupCommandResultItemDto> Results { get; set; } = new();
}

public class GroupCommandResultItemDto
{
    public string ScreenId { get; set; } = string.Empty;

    public bool Success { get; set; }

    public Guid? CommandId { get; set; }

    public string? Error { get; set; }
}
