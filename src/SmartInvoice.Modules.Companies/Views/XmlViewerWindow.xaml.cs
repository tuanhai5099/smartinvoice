using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Xml;

namespace SmartInvoice.Modules.Companies.Views;

/// <summary>Mục trong cây XML: có thể đóng/mở để xem tag cha, tag con.</summary>
public class XmlNodeItem
{
    public string DisplayName { get; set; } = "";
    public string? ToolTipText { get; set; }
    public ObservableCollection<XmlNodeItem> Children { get; } = new();
}

public partial class XmlViewerWindow : Window
{
    public XmlViewerWindow()
    {
        InitializeComponent();
    }

    public void LoadFile(string filePath)
    {
        PathText.Text = filePath;
        try
        {
            if (!File.Exists(filePath))
            {
                SetError("File không tồn tại.");
                return;
            }

            var raw = File.ReadAllText(filePath);
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(raw);

                var sb = new StringBuilder();
                var settings = new XmlWriterSettings
                {
                    Indent = true,
                    IndentChars = "  ",
                    NewLineChars = "\r\n",
                    NewLineHandling = NewLineHandling.Replace
                };
                using (var writer = XmlWriter.Create(sb, settings))
                    doc.Save(writer);
                XmlText.Text = sb.ToString();

                var rootList = new ObservableCollection<XmlNodeItem>();
                if (doc.DocumentElement != null)
                {
                    var rootItem = BuildNodeItem(doc.DocumentElement);
                    if (rootItem != null)
                        rootList.Add(rootItem);
                }
                XmlTree.ItemsSource = rootList;
            }
            catch
            {
                XmlText.Text = raw;
                SetError("XML không hợp lệ. Xem tab Nguồn.");
            }
        }
        catch (Exception ex)
        {
            SetError("Lỗi đọc file: " + ex.Message);
        }
    }

    private void SetError(string message)
    {
        XmlText.Text = message;
        XmlTree.ItemsSource = null;
    }

    private static XmlNodeItem? BuildNodeItem(XmlNode node)
    {
        if (node is XmlElement el)
        {
            var attrs = new StringBuilder();
            if (el.Attributes?.Count > 0)
            {
                foreach (XmlAttribute a in el.Attributes)
                {
                    if (attrs.Length > 0) attrs.Append(", ");
                    var val = a.Value?.Length > 40 ? a.Value[..40] + "…" : a.Value;
                    attrs.Append(a.Name).Append("=\"").Append(val).Append("\"");
                }
            }
            var display = string.IsNullOrEmpty(attrs.ToString())
                ? "<" + el.Name + ">"
                : "<" + el.Name + " " + attrs + ">";
            var item = new XmlNodeItem
            {
                DisplayName = display,
                ToolTipText = el.OuterXml?.Length > 500 ? el.OuterXml[..500] + "…" : el.OuterXml
            };
            foreach (XmlNode child in el.ChildNodes)
            {
                var childItem = BuildNodeItem(child);
                if (childItem != null)
                    item.Children.Add(childItem);
            }
            return item;
        }
        if (node is XmlText text)
        {
            var t = text.Value?.Trim();
            if (string.IsNullOrEmpty(t)) return null;
            var display = t.Length > 80 ? t[..80] + "…" : t;
            return new XmlNodeItem
            {
                DisplayName = display,
                ToolTipText = t
            };
        }
        if (node is XmlComment comment)
            return new XmlNodeItem { DisplayName = "<!-- " + (comment.Value?.Trim().Length > 60 ? comment.Value.Trim()[..60] + "…" : comment.Value?.Trim()) + " -->", ToolTipText = comment.Value };
        return null;
    }

    private void ExpandAllButton_Click(object sender, RoutedEventArgs e)
    {
        SetExpandCollapse(XmlTree, true);
    }

    private void CollapseAllButton_Click(object sender, RoutedEventArgs e)
    {
        SetExpandCollapse(XmlTree, false);
    }

    private static void SetExpandCollapse(ItemsControl parent, bool expand)
    {
        if (parent is TreeView tv)
        {
            foreach (var item in tv.Items)
            {
                if (tv.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi)
                {
                    tvi.IsExpanded = expand;
                    ExpandCollapseRecursive(tvi, expand);
                }
            }
        }
    }

    private static void ExpandCollapseRecursive(TreeViewItem parent, bool expand)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi)
            {
                tvi.IsExpanded = expand;
                ExpandCollapseRecursive(tvi, expand);
            }
        }
    }
}
