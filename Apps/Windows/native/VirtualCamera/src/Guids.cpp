// Instantiates the MF GUIDs that aren't in mfuuid.lib (e.g. the MF_VIRTUALCAMERA_* attributes).
// INITGUID definitions are selectany, so overlaps with mfuuid.lib are harmless. KS headers
// stay out of this file: ks.h redefines GUID_NULL as a __uuidof expression.
#include <windows.h>
#include <unknwn.h>
#include <initguid.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfvirtualcamera.h>
