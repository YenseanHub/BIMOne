namespace RevitModeler.Core.Models;

/// <summary>建模动作：决定 Revit 侧调用哪类创建 API。</summary>
public enum ModelAction
{
    CreateWall,    // 占位：线 → 墙
    CreateFloor,   // 占位：闭合多段线 → 楼板
    PlaceFamily,   // 精化：块/文字 → 族实例
    Skip           // 忽略（填充、标注线等）
}
