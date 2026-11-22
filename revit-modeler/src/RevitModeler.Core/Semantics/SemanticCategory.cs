namespace RevitModeler.Core.Semantics;

/// <summary>从 CAD 图元推断出的建筑语义类别（不依赖项目级规则表）。</summary>
public enum SemanticCategory
{
    Wall, Floor, Door, Window, Column, Annotation, Furniture, Finish, Unknown
}
