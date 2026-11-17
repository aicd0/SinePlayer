// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

using Microsoft.UI.Input;

using Windows.Win32;

namespace SinePlayer.Common.Utils;

/// <summary>
/// Makes the cursor the app hides the pointer with. <see cref="Microsoft.UI.Xaml.UIElement.ProtectedCursor"/>
/// only takes a cursor object — there is no hidden state and no transparent system shape — so hiding means
/// assigning a cursor that draws nothing. A disposed one, which was used before, is not a cursor the input
/// stack can apply: it leaves the one that was on screen, so the pointer came back as soon as anything set a
/// cursor of its own.
/// </summary>
internal static partial class CursorUtils
{
    private const string INPUT_CURSOR_CLASS_ID = "Microsoft.UI.Input.InputCursor";

    public static InputCursor CreateTransparentCursor()
    {
        unsafe
        {
            // A black-and-white cursor keeps the screen where the AND mask has a bit set and inverts it where
            // the XOR mask has one, so all ones in the AND mask and all zeroes in the XOR mask paint nothing.
            // Mask rows are padded to a WORD; 1x1 is therefore two bytes each.
            byte* andMask = stackalloc byte[] { 0xFF, 0x00 };
            byte* xorMask = stackalloc byte[] { 0x00, 0x00 };

            using DestroyCursorSafeHandle cursor = PInvoke.CreateCursor(null, 0, 0, 1, 1, andMask, xorMask);
            if (cursor.IsInvalid)
            {
                throw new InvalidOperationException("A cursor that draws nothing could not be created.");
            }

            return CreateCursorFromHandle(cursor.DangerousGetHandle());
        }
    }

    private static InputCursor CreateCursorFromHandle(nint handle)
    {
        PInvoke.WindowsCreateString(INPUT_CURSOR_CLASS_ID, (uint)INPUT_CURSOR_CLASS_ID.Length, out WindowsDeleteStringSafeHandle classId).ThrowOnFailure();
        using (classId)
        {
            PInvoke.RoGetActivationFactory(classId, out IInputCursorStaticsInterop statics).ThrowOnFailure();

            int result = statics.CreateFromHCursor(handle, out nint cursor);
            Marshal.ThrowExceptionForHR(result);

            // WinUI copies the cursor Win32 made, so that handle is only borrowed for the length of this call.
            return WinRT.MarshalInspectable<InputCursor>.FromAbi(cursor);
        }
    }

    // WinUI ships this interop interface in a header of its own rather than in the Win32 metadata, so no
    // generator has it: it is declared here, and the activation factory that reaches it comes from CsWin32.
    [GeneratedComInterface, Guid("ac6f5065-90c4-46ce-beb7-05e138e54117"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IInputCursorStaticsInterop
    {
        void GetIids();

        void GetRuntimeClassName();

        void GetTrustLevel();

        [PreserveSig]
        int CreateFromHCursor(nint cursor, out nint result);
    }
}
