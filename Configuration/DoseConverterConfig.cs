﻿//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//     Runtime Version:4.0.30319.42000
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------

// 
// This source code was auto-generated by xsd, Version=4.8.3928.0.
// 
namespace DoseConverter {
    using System.Xml.Serialization;
    
    
    /// <remarks/>
    [System.CodeDom.Compiler.GeneratedCodeAttribute("xsd", "4.8.3928.0")]
    [System.SerializableAttribute()]
    [System.Diagnostics.DebuggerStepThroughAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType=true)]
    [System.Xml.Serialization.XmlRootAttribute(Namespace="", IsNullable=false)]
    public partial class DoseConverterConfig {
        
        private DoseConverterConfigVersion versionField;
        
        private DoseConverterConfigDefaults defaultsField;
        
        private DoseConverterConfigStructure[] structuresField;
        
        /// <remarks/>
        public DoseConverterConfigVersion version {
            get {
                return this.versionField;
            }
            set {
                this.versionField = value;
            }
        }
        
        /// <remarks/>
        public DoseConverterConfigDefaults Defaults {
            get {
                return this.defaultsField;
            }
            set {
                this.defaultsField = value;
            }
        }
        
        /// <remarks/>
        [System.Xml.Serialization.XmlArrayItemAttribute("Structure", IsNullable=false)]
        public DoseConverterConfigStructure[] Structures {
            get {
                return this.structuresField;
            }
            set {
                this.structuresField = value;
            }
        }
    }
    
    /// <remarks/>
    [System.CodeDom.Compiler.GeneratedCodeAttribute("xsd", "4.8.3928.0")]
    [System.SerializableAttribute()]
    [System.Diagnostics.DebuggerStepThroughAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType=true)]
    public partial class DoseConverterConfigVersion {
        
        private string numberField;
        
        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string number {
            get {
                return this.numberField;
            }
            set {
                this.numberField = value;
            }
        }
    }
    
    /// <remarks/>
    [System.CodeDom.Compiler.GeneratedCodeAttribute("xsd", "4.8.3928.0")]
    [System.SerializableAttribute()]
    [System.Diagnostics.DebuggerStepThroughAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType=true)]
    public partial class DoseConverterConfigDefaults {
        
        private double alphaBetaRatioField;
        
        private string tempEdgeStructureNameField;
        
        private string tempStructureSetNameField;
        
        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public double AlphaBetaRatio {
            get {
                return this.alphaBetaRatioField;
            }
            set {
                this.alphaBetaRatioField = value;
            }
        }
        
        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string TempEdgeStructureName {
            get {
                return this.tempEdgeStructureNameField;
            }
            set {
                this.tempEdgeStructureNameField = value;
            }
        }
        
        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string TempStructureSetName {
            get {
                return this.tempStructureSetNameField;
            }
            set {
                this.tempStructureSetNameField = value;
            }
        }
    }
    
    /// <remarks/>
    [System.CodeDom.Compiler.GeneratedCodeAttribute("xsd", "4.8.3928.0")]
    [System.SerializableAttribute()]
    [System.Diagnostics.DebuggerStepThroughAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType=true)]
    public partial class DoseConverterConfigStructure {
        
        private DoseConverterConfigStructureAlias[] aliasesField;
        
        private string structureLabelField;
        
        private double maxEQD2Field;
        
        private bool maxEQD2FieldSpecified;
        
        private double alphaBetaRatioField;
        
        private bool forceEdgeConversionField;
        
        public DoseConverterConfigStructure() {
            this.forceEdgeConversionField = true;
        }
        
        /// <remarks/>
        [System.Xml.Serialization.XmlArrayItemAttribute("Alias", IsNullable=false)]
        public DoseConverterConfigStructureAlias[] Aliases {
            get {
                return this.aliasesField;
            }
            set {
                this.aliasesField = value;
            }
        }
        
        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string StructureLabel {
            get {
                return this.structureLabelField;
            }
            set {
                this.structureLabelField = value;
            }
        }
        
        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public double MaxEQD2 {
            get {
                return this.maxEQD2Field;
            }
            set {
                this.maxEQD2Field = value;
            }
        }
        
        /// <remarks/>
        [System.Xml.Serialization.XmlIgnoreAttribute()]
        public bool MaxEQD2Specified {
            get {
                return this.maxEQD2FieldSpecified;
            }
            set {
                this.maxEQD2FieldSpecified = value;
            }
        }
        
        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public double AlphaBetaRatio {
            get {
                return this.alphaBetaRatioField;
            }
            set {
                this.alphaBetaRatioField = value;
            }
        }
        
        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        [System.ComponentModel.DefaultValueAttribute(true)]
        public bool ForceEdgeConversion {
            get {
                return this.forceEdgeConversionField;
            }
            set {
                this.forceEdgeConversionField = value;
            }
        }
    }
    
    /// <remarks/>
    [System.CodeDom.Compiler.GeneratedCodeAttribute("xsd", "4.8.3928.0")]
    [System.SerializableAttribute()]
    [System.Diagnostics.DebuggerStepThroughAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType=true)]
    public partial class DoseConverterConfigStructureAlias {
        
        private string structureIdField;
        
        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string StructureId {
            get {
                return this.structureIdField;
            }
            set {
                this.structureIdField = value;
            }
        }
    }
}
