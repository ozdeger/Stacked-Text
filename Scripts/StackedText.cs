using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

[ExecuteInEditMode]
public class StackedText : MonoBehaviour
{
    #region Fields

    private const string RequiredShaderName = "TextMeshPro/Distance Field Dilate";

    [SerializeField] private TMP_Text Text;
    [SerializeField] private bool ShowMainText = true;
    [Range(0, 1)] public float MainTextSoftness;
    [Range(-1, 1f)] public float MainTextDilate;
    [SerializeField] public List<StackConfig> Stacks = new();

    [Header("Curve Settings")]
    [SerializeField] private bool UseCurve;
    [SerializeField] private AnimationCurve Curve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
    [SerializeField] private float CurveScale = 1f;
    [SerializeField] private bool KeepTextCentered;
    [SerializeField] private float ReferenceWidth;
#if UNITY_EDITOR
    [SerializeField] private bool DrawCurveGizmos = true;
#endif

    private Mesh _cachedMesh;
    private readonly List<Vector3> _curveOffsets = new();
    private readonly List<Vector3> _sourceVerts = new();
    private readonly List<Color32> _sourceColors = new();
    private readonly List<Vector2> _sourceUVs = new();
    private readonly List<Vector2> _sourceUV2s = new();
    private readonly List<int> _sourceTris = new();
    private readonly List<Vector3> _outVerts = new();
    private readonly List<Color32> _outColors = new();
    private readonly List<Vector2> _outUVs = new();
    private readonly List<Vector2> _outUV2s = new();
    private readonly List<Vector2> _outUV3s = new();
    private readonly List<int> _outTris = new();
    private bool _lastShowMainText;
    private float _lastMainTextDilate;
    private float _lastMainTextSoftness;
    private readonly List<StackConfig> _lastStacks = new();
    private Vector3 _lastLossyScale;
    private bool _forceUpdateNextFrame;

    #endregion

    #region Editor - Material Validation

#if UNITY_EDITOR
    private void OnValidate()
    {
        Text ??= GetComponent<TMP_Text>();
        UnityEditor.EditorApplication.delayCall += ValidateMaterial;
        EnsureShaderChannels();
        _forceUpdateNextFrame = true;
    }

    private void ValidateMaterial()
    {
        UnityEditor.EditorApplication.delayCall -= ValidateMaterial;
        
        if (Text == null || Text.font == null)
            return;

        if (HasCompatibleShader(Text.fontSharedMaterial))
            return;

        var compatibleMat = GetOrCreateCompatibleMaterial(Text.font);
        if (compatibleMat == null)
            return;

        Text.fontSharedMaterial = compatibleMat;
        Text.SetVerticesDirty();
    }

    private static bool HasCompatibleShader(Material material)
    {
        return material != null &&
            material.shader != null &&
            material.shader.name == RequiredShaderName;
    }

    private static Material GetOrCreateCompatibleMaterial(TMP_FontAsset font)
    {
        if (font.atlasTextures == null || font.atlasTextures.Length == 0)
            return null;

        var fontAtlas = font.atlasTextures[0];
        var fontPath = UnityEditor.AssetDatabase.GetAssetPath(font);
        var fontDir = System.IO.Path.GetDirectoryName(fontPath)?.Replace('\\', '/');

        if (string.IsNullOrEmpty(fontDir))
            return null;

        var guids = UnityEditor.AssetDatabase.FindAssets("t:Material", new[] { fontDir });
        foreach (var guid in guids)
        {
            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            var mat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(path);
            if (HasCompatibleShader(mat) && mat.GetTexture("_MainTex") == fontAtlas)
                return mat;
        }

        var shader = Shader.Find(RequiredShaderName);
        if (shader == null)
        {
            Debug.LogError($"[StackedText] Shader '{RequiredShaderName}' not found in project.");
            return null;
        }

        var newMat = new Material(font.material) { shader = shader };
        var matPath = $"{fontDir}/{font.name} - StackedText.mat";
        matPath = UnityEditor.AssetDatabase.GenerateUniqueAssetPath(matPath);
        UnityEditor.AssetDatabase.CreateAsset(newMat, matPath);
        UnityEditor.AssetDatabase.SaveAssets();

        Debug.Log($"[StackedText] Created material at '{matPath}'.", newMat);
        return newMat;
    }
#endif

    #endregion

    #region Lifecycle

    private void OnEnable()
    {
        Canvas.willRenderCanvases += OnPreRenderCanvas;
        TMPro_EventManager.TEXT_CHANGED_EVENT.Add(OnTextChanged);

        EnsureShaderChannels();

        _forceUpdateNextFrame = true;
    }

    private void OnDisable()
    {
        Canvas.willRenderCanvases -= OnPreRenderCanvas;
        TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(OnTextChanged);
        
        if (Text != null)
            Text.ForceMeshUpdate();
    }

    private void EnsureShaderChannels()
    {
        if (Text == null || Text.canvas == null)
            return;
        if (!Text.canvas.additionalShaderChannels.HasFlag(AdditionalCanvasShaderChannels.TexCoord3))
        {
            Text.canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord3;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(Text.canvas);
#endif
        }
    }

    private void OnRectTransformDimensionsChange()
    {
        // Mark for update, but don't execute yet to avoid race condition with TMP
        _forceUpdateNextFrame = true;
    }

    private void OnTextChanged(Object changedText)
    {
        if (changedText != Text)
            return;
        _forceUpdateNextFrame = true;
    }

    #endregion

    #region Mesh Generation
    
    // Runs right before canvas renders to keep mesh in sync, fixing race conditions with TMP
    private void OnPreRenderCanvas()
    {
        if (Text == null || !enabled)
            return;

        bool scaleChanged = transform.lossyScale != _lastLossyScale;
        bool meshOverwritten = _cachedMesh != null && Text.canvasRenderer.GetMesh() != _cachedMesh;

        if (_forceUpdateNextFrame || scaleChanged || meshOverwritten || HasPropertiesChanged())
        {
            GenerateStackedText();
            _forceUpdateNextFrame = false;
            _lastLossyScale = transform.lossyScale;
        }
    }

    private void GenerateStackedText()
    {
        if (Text == null)
            return;

        Text.ForceMeshUpdate();

        TMP_TextInfo textInfo = Text.textInfo;

        if (textInfo == null || textInfo.characterCount == 0)
            return;

        Mesh sourceMesh = Text.mesh;

        if (sourceMesh == null || sourceMesh.vertexCount == 0)
            return;

        PopulateInvalidStackConfigs();

        var isExtraPaddingRequired = IsExtraPaddingRequired();
        if (isExtraPaddingRequired != Text.extraPadding)
            Text.extraPadding = isExtraPaddingRequired;

        // Non-allocating mesh data reads into cached lists
        sourceMesh.GetVertices(_sourceVerts);
        sourceMesh.GetColors(_sourceColors);
        sourceMesh.GetUVs(0, _sourceUVs);
        sourceMesh.GetUVs(1, _sourceUV2s);
        sourceMesh.GetTriangles(_sourceTris, 0);

        int sourceVCount = _sourceVerts.Count;
        int sourceTriCount = _sourceTris.Count;
        int totalLayers = 1;
        foreach (var stackConfig in Stacks)
        {
            if (!stackConfig.Enabled)
                continue;
            totalLayers += stackConfig.StackCount;
        }

        if (sourceVCount * totalLayers > 65000)
        {
            if (Application.isEditor)
                Debug.LogWarning("[StackedText] Vertex limit exceeded.");
            return;
        }

        // --- APPLY CURVE OFFSETS ---
        if (UseCurve && TryGetCurvedVertexOffsets(Text, Curve, CurveScale, ReferenceWidth, KeepTextCentered, _curveOffsets))
        {
            int loopCount = Mathf.Min(sourceVCount, _curveOffsets.Count);
            for (int v = 0; v < loopCount; v++)
                _sourceVerts[v] += _curveOffsets[v];
        }

        // --- PREPARE OUTPUT ---
        int totalVertCount = sourceVCount * totalLayers;
        int totalTriCount = sourceTriCount * totalLayers;
        ClearAndEnsureCapacity(_outVerts, totalVertCount);
        ClearAndEnsureCapacity(_outColors, totalVertCount);
        ClearAndEnsureCapacity(_outUVs, totalVertCount);
        ClearAndEnsureCapacity(_outUV2s, totalVertCount);
        ClearAndEnsureCapacity(_outUV3s, totalVertCount);
        ClearAndEnsureCapacity(_outTris, totalTriCount);

        // --- STACK LAYERS ---
        for (int s = Stacks.Count - 1; s >= 0; s--)
        {
            var stackConfig = Stacks[s];
            if (!stackConfig.Enabled)
                continue;

            GetNormalizedSoftnessAndDilate(stackConfig.Dilate, stackConfig.Softness, out float dilate, out float softness);
            var uv3 = new Vector2(dilate, softness);
            var stackCount = stackConfig.StackCount;
            for (int i = stackCount; i >= 1; i--)
            {
                float t = stackCount == 1 ? 1 : (i - 1) / ((float)stackCount - 1);
                Color32 layerColor = stackConfig.Color.Evaluate(t);
                Vector3 currentOffset = stackConfig.GetOffset(t);
                int currentLayerVertStart = _outVerts.Count;

                for (int v = 0; v < sourceVCount; v++)
                {
                    _outVerts.Add(_sourceVerts[v] + currentOffset);
                    _outUVs.Add(_sourceUVs[v]);
                    _outUV2s.Add(_sourceUV2s[v]);
                    _outUV3s.Add(uv3);
                    _outColors.Add(layerColor);
                }

                for (int tIdx = 0; tIdx < sourceTriCount; tIdx++)
                    _outTris.Add(_sourceTris[tIdx] + currentLayerVertStart);
            }
        }

        GetNormalizedSoftnessAndDilate(MainTextDilate, MainTextSoftness, out float mainDilate, out float mainSoftness);

        // --- MAIN TEXT LAYER ---
        int mainTextVertStart = _outVerts.Count;
        Color32 mainFallbackColor = Stacks.Count > 0 ? (Color32)Stacks[0].Color.Evaluate(0) : default;
        var mainUV3 = new Vector2(mainDilate, mainSoftness);
        for (int v = 0; v < sourceVCount; v++)
        {
            _outVerts.Add(_sourceVerts[v]);
            _outUVs.Add(_sourceUVs[v]);
            _outUV2s.Add(_sourceUV2s[v]);
            _outUV3s.Add(mainUV3);
            _outColors.Add(ShowMainText ? _sourceColors[v] : mainFallbackColor);
        }

        for (int tIdx = 0; tIdx < sourceTriCount; tIdx++)
            _outTris.Add(_sourceTris[tIdx] + mainTextVertStart);

        // --- ASSIGN TO MESH ---
        if (_cachedMesh == null)
        {
            _cachedMesh = new Mesh();
            _cachedMesh.MarkDynamic();
        }

        _cachedMesh.Clear();
        _cachedMesh.SetVertices(_outVerts);
        _cachedMesh.SetColors(_outColors);
        _cachedMesh.SetUVs(0, _outUVs);
        _cachedMesh.SetUVs(1, _outUV2s);
        _cachedMesh.SetUVs(3, _outUV3s);
        _cachedMesh.SetTriangles(_outTris, 0);
        _cachedMesh.RecalculateBounds();

        Text.canvasRenderer.SetMesh(_cachedMesh);

        SaveLastUsedProperties();
    }

    private static void ClearAndEnsureCapacity<T>(List<T> list, int capacity)
    {
        list.Clear();
        if (list.Capacity < capacity)
            list.Capacity = capacity;
    }

    private void PopulateInvalidStackConfigs()
    {
        for (int i = Stacks.Count - 1; i >= 0; i--)
        {
            if (!Stacks[i].IsInvalid())
                continue;
            Stacks[i] = StackConfig.CreateDefault();
        }
    }

    private bool HasPropertiesChanged()
    {
        if (!Mathf.Approximately(_lastMainTextDilate, MainTextDilate))
            return true;
        if (!Mathf.Approximately(_lastMainTextSoftness, MainTextSoftness))
            return true;
        if (_lastShowMainText != ShowMainText)
            return true;
        if (_lastStacks.Count != Stacks.Count)
            return true;

        for (int i = 0; i < Stacks.Count; i++)
        {
            if (_lastStacks[i].HasChanged(Stacks[i]))
                return true;
        }
        return false;
    }

    private bool IsExtraPaddingRequired()
    {
        if (Text == null || Text.canvas == null)
            return false;

        var totalDilate = MathF.Abs(MainTextDilate);
        var totalSoftness = MathF.Abs(MainTextSoftness);
        foreach (var stack in Stacks)
        {
            if (!stack.Enabled)
                continue;
            totalDilate += MathF.Abs(stack.Dilate);
            totalSoftness += MathF.Abs(stack.Softness);
        }

        return totalDilate + totalSoftness > 0.001f;
    }

    #endregion

    #region Curve

    public static bool TryGetCurvedVertexOffsets(TMP_Text text, AnimationCurve curve, float curveScale, float referenceWidth, bool stabilizeY, List<Vector3> vertexOffsets)
    {
        text.ForceMeshUpdate(true, true);
        TMP_TextInfo textInfo = text.textInfo;
        int characterCount = textInfo.characterCount;

        if (characterCount == 0 || textInfo.meshInfo == null || textInfo.meshInfo.Length == 0 || textInfo.meshInfo[0].vertices == null)
            return false;

        GetBounds(text, referenceWidth, out var boundsMaxX, out var boundsMinX);

        int requiredLength = textInfo.meshInfo[0].vertices.Length;
        vertexOffsets.Clear();
        if (vertexOffsets.Capacity < requiredLength)
            vertexOffsets.Capacity = requiredLength;
        for (int i = 0; i < requiredLength; i++)
            vertexOffsets.Add(default);

        float yGlobalOffset = 0;
        if (stabilizeY)
            yGlobalOffset = curve.Evaluate(0.5f) * text.bounds.size.x * curveScale * 0.1f;

        for (int i = 0; i < characterCount; i++)
        {
            var charInfo = textInfo.characterInfo[i];
            if (!charInfo.isVisible)
                continue;

            int vertexIndex = charInfo.vertexIndex;
            int materialIndex = charInfo.materialReferenceIndex;

            if (materialIndex >= textInfo.meshInfo.Length)
                continue;

            var sourceVertices = textInfo.meshInfo[materialIndex].vertices;

            if (vertexIndex + 3 >= sourceVertices.Length)
                continue;

            Vector3 v0 = sourceVertices[vertexIndex + 0];
            Vector3 v1 = sourceVertices[vertexIndex + 1];
            Vector3 v2 = sourceVertices[vertexIndex + 2];
            Vector3 v3 = sourceVertices[vertexIndex + 3];

            Vector3 offsetToMidBaseline = new Vector3((v0.x + v2.x) / 2, charInfo.baseLine, 0);

            float x0 = (offsetToMidBaseline.x - boundsMinX) / (boundsMaxX - boundsMinX);
            float x1 = x0 + 0.0001f;
            float y0 = curve.Evaluate(1 - x0) * text.bounds.size.x * curveScale * 0.1f;
            float y1 = curve.Evaluate(1 - x1) * text.bounds.size.x * curveScale * 0.1f;

            Vector3 horizontal = Vector3.right;
            Vector3 tangent = new Vector3(x1 * (boundsMaxX - boundsMinX) + boundsMinX, y1) -
                              new Vector3(offsetToMidBaseline.x, y0);

            float dot = Mathf.Acos(Vector3.Dot(horizontal, tangent.normalized)) * Mathf.Rad2Deg;
            Vector3 cross = Vector3.Cross(horizontal, tangent);
            float angle = cross.z > 0 ? dot : 360 - dot;

            Matrix4x4 matrix = Matrix4x4.TRS(new Vector3(0, y0 - yGlobalOffset, 0), Quaternion.Euler(0, 0, angle), Vector3.one);

            Vector3 t0 = matrix.MultiplyPoint3x4(v0 - offsetToMidBaseline) + offsetToMidBaseline;
            Vector3 t1 = matrix.MultiplyPoint3x4(v1 - offsetToMidBaseline) + offsetToMidBaseline;
            Vector3 t2 = matrix.MultiplyPoint3x4(v2 - offsetToMidBaseline) + offsetToMidBaseline;
            Vector3 t3 = matrix.MultiplyPoint3x4(v3 - offsetToMidBaseline) + offsetToMidBaseline;

            vertexOffsets[vertexIndex + 0] = t0 - v0;
            vertexOffsets[vertexIndex + 1] = t1 - v1;
            vertexOffsets[vertexIndex + 2] = t2 - v2;
            vertexOffsets[vertexIndex + 3] = t3 - v3;
        }
        return true;
    }

    private static void GetBounds(TMP_Text text, float referenceWidth, out float boundsMaxX, out float boundsMinX)
    {
        boundsMinX = text.bounds.min.x;
        boundsMaxX = text.bounds.max.x;

        if (referenceWidth > 0)
        {
            boundsMinX = Mathf.Min(boundsMinX, -referenceWidth / 2f);
            boundsMaxX = Mathf.Max(boundsMaxX, referenceWidth / 2f);
        }
    }

    #endregion

    #region Public API

    private void SaveLastUsedProperties()
    {
        _lastShowMainText = ShowMainText;
        _lastMainTextDilate = MainTextDilate;
        _lastMainTextSoftness = MainTextSoftness;
        _lastStacks.Clear();
        _lastStacks.AddRange(Stacks);
    }
    
    public void SetStacks(List<StackConfig> stacks)
    {
        if (stacks == null || stacks == Stacks)
            return;
        
        Stacks = stacks;
        _forceUpdateNextFrame = true;
        Text.ForceMeshUpdate();
    }

    public void GetNormalizedSoftnessAndDilate(float dilate, float softness, out float normalizedDilate, out float normalizedSoftness)
    {
        var total = MathF.Max(softness + dilate, 1);
        normalizedDilate = dilate / total * 0.85f;
        normalizedSoftness = softness / total * 0.85f;
    }

    #endregion

    #region Editor - Gizmos

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!enabled || Text == null || !UseCurve || !DrawCurveGizmos)
            return;

        var color = Gizmos.color;
        var boundsSize = Text.bounds.size;
        var lossyScale = transform.lossyScale;
        var gizmoPosition = transform.position - new Vector3(0, boundsSize.y / 2f * lossyScale.y, 0);
        GetBounds(Text, ReferenceWidth, out var minX, out var maxX);
        var pointA = gizmoPosition + new Vector3(minX * lossyScale.x, 0, 0);
        var pointB = gizmoPosition + new Vector3(maxX * lossyScale.x, 0, 0);
        var offsetAxis = new Vector3(0, Vector2.Distance(pointA, pointB), 0);
        Gizmos.color = Color.magenta;
        DrawAnimationCurveGizmo(Curve, pointA, pointB, offsetAxis, CurveScale * 0.1f);

        if (ReferenceWidth > 0)
        {
            var width = ReferenceWidth * lossyScale.x;
            Gizmos.color = Color.green;
            Gizmos.DrawWireCube(gizmoPosition, new(width, 1, 1));
            Gizmos.color = color;
        }
    }

    private static void DrawAnimationCurveGizmo(AnimationCurve curve, Vector3 pointA, Vector3 pointB, Vector3 offsetAxis, float curveScale, int resolution = 20)
    {
        if (curve == null || curve.length == 0 || resolution < 2)
        {
            Gizmos.DrawLine(pointA, pointB);
            return;
        }

        var previousPoint = pointA + offsetAxis * curve.Evaluate(0f) * curveScale;
        for (int i = 1; i <= resolution; i++)
        {
            float t = (float)i / resolution;
            var curvePos = Vector3.Lerp(pointA, pointB, t) + offsetAxis * curve.Evaluate(t) * curveScale;
            Gizmos.DrawLine(previousPoint, curvePos);
            previousPoint = curvePos;
        }
    }
#endif

    #endregion

    #region StackConfig

    [Serializable]
    public struct StackConfig
    {
        public bool Enabled;
        [Range(1, 6)] public int StackCount;
        public Gradient Color;
        public Vector2 StartOffset;
        public Vector2 EndOffset;
        [Range(0, 1)] public float Softness;
        [Range(-1f, 1f)] public float Dilate;

        public static StackConfig CreateDefault()
        {
            return new StackConfig
            {
                Enabled = true,
                StackCount = 1,
                Color = new Gradient()
                {
                    colorKeys = new GradientColorKey[]
                    {
                        new(UnityEngine.Color.white, 0f),
                        new(UnityEngine.Color.black, 1f),
                    },
                    alphaKeys = new GradientAlphaKey[]
                    {
                        new(1f, 0f),
                        new(1f, 1f),
                    },
                },
                EndOffset = new Vector2(2f, -2f),
            };
        }

        public Vector2 GetOffset(float t)
        {
            return Vector2.Lerp(StartOffset, EndOffset, t);
        }

        public bool IsInvalid()
        {
            return StackCount < 1;
        }

        public bool HasChanged(StackConfig other)
        {
            return Enabled != other.Enabled ||
                StackCount != other.StackCount ||
                !Mathf.Approximately(Dilate, other.Dilate) ||
                !Mathf.Approximately(Softness, other.Softness) ||
                StartOffset != other.StartOffset ||
                EndOffset != other.EndOffset ||
                !Color.Equals(other.Color);
        }
    }

    #endregion
}