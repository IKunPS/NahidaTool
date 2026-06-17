using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NahidaTool.Models.Service;

namespace NahidaTool.Models.Helper;

/// <summary>
/// 使用 Win32 API 实现文件夹选择器，适用于非打包 WinUI 3 应用
/// </summary>
public static class FolderPickerHelper
{
    [ComImport]
    [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    [ComImport]
    [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr hwndOwner);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IntPtr psi);
        void SetFolder(IntPtr psi);
        void GetFolder(out IntPtr ppsi);
        void GetCurrentSelection(out IntPtr ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IntPtr psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    private enum SIGDN : uint
    {
        SIGDN_FILESYSPATH = 0x80058000
    }

    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_NOVALIDATE = 0x00000100;
    private const uint FOS_NOTESTFILECREATE = 0x00010000;
    private const uint FOS_DONTADDTORECENT = 0x02000000;
    private const uint FOS_FILEMUSTEXIST = 0x00001000;
    private const uint FOS_PATHMUSTEXIST = 0x00000800;

    /// <summary>
    /// 异步打开文件夹选择对话框（在独立STA线程运行，不阻塞UI线程）
    /// </summary>
    /// <param name="hwnd">父窗口句柄</param>
    /// <param name="title">对话框标题</param>
    /// <returns>选择的文件夹路径，如果取消则返回 null</returns>
    public static Task<string?> PickFolderAsync(IntPtr hwnd, string title = "选择文件夹")
    {
        var tcs = new TaskCompletionSource<string?>();

        var thread = new Thread(() =>
        {
            tcs.SetResult(PickFolderCore(hwnd, title));
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return tcs.Task;
    }

    /// <summary>
    /// 异步打开文件选择对话框（在独立STA线程运行）
    /// </summary>
    /// <param name="hwnd">父窗口句柄</param>
    /// <param name="filter">文件扩展名过滤，如 ".exe" 或 "所有文件|*.*"</param>
    /// <param name="title">对话框标题</param>
    /// <returns>选择的文件路径，如果取消则返回 null</returns>
    public static Task<string?> PickFileAsync(IntPtr hwnd, string filter = "*.*", string title = "选择文件")
    {
        var tcs = new TaskCompletionSource<string?>();

        var thread = new Thread(() =>
        {
            tcs.SetResult(PickFileCore(hwnd, filter, title));
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return tcs.Task;
    }

    private static string? PickFolderCore(IntPtr hwnd, string title)
    {
        IFileOpenDialog? dialog = null;
        try
        {
            dialog = (IFileOpenDialog)new FileOpenDialog();
            dialog.SetOptions(FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_NOVALIDATE | FOS_NOTESTFILECREATE | FOS_DONTADDTORECENT);
            dialog.SetTitle(title);

            int hr = dialog.Show(hwnd);
            if (hr < 0)
            {
                return null;
            }

            dialog.GetResult(out IShellItem item);
            item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out string path);
            return path;
        }
        catch (Exception ex)
        {
            LogService.Debug($"文件夹选择对话框失败: {ex.Message}");
            return null;
        }
        finally
        {
            if (dialog != null)
            {
                Marshal.ReleaseComObject(dialog);
            }
        }
    }

    private static string? PickFileCore(IntPtr hwnd, string filter, string title)
    {
        IFileOpenDialog? dialog = null;
        try
        {
            dialog = (IFileOpenDialog)new FileOpenDialog();
            dialog.SetOptions(FOS_FORCEFILESYSTEM | FOS_FILEMUSTEXIST | FOS_PATHMUSTEXIST | FOS_DONTADDTORECENT);
            dialog.SetTitle(title);

            if (!string.IsNullOrEmpty(filter))
            {
                // 支持 "显示名称|*.ext" 格式，也兼容直接传 "*.ext"
                string name, spec;
                int sep = filter.IndexOf('|');
                if (sep > 0)
                {
                    name = filter[..sep];
                    spec = filter[(sep + 1)..];
                }
                else
                {
                    name = filter;
                    spec = filter;
                }
                var filterSpec = new COMDLG_FILTERSPEC
                {
                    pszName = name,
                    pszSpec = spec
                };
                var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(filterSpec));
                Marshal.StructureToPtr(filterSpec, ptr, false);
                dialog.SetFileTypes(1, ptr);
                Marshal.FreeHGlobal(ptr);
            }

            int hr = dialog.Show(hwnd);
            if (hr < 0)
            {
                return null;
            }

            dialog.GetResult(out IShellItem item);
            item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out string path);
            return path;
        }
        catch (Exception ex)
        {
            LogService.Debug($"文件选择对话框失败: {ex.Message}");
            return null;
        }
        finally
        {
            if (dialog != null)
            {
                Marshal.ReleaseComObject(dialog);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pszName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pszSpec;
    }
}
