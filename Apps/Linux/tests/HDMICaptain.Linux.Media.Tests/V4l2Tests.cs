using HDMICaptain.Core;
using HDMICaptain.Linux.Media.Capture;

namespace HDMICaptain.Linux.Media.Tests;

public unsafe class V4l2Tests
{
    [Fact]
    public void MirrorStructsMatchTheCLayout()
    {
        Assert.Equal(V4l2Native.hc_v4l2_struct_size(0), sizeof(V4l2Native.Info));
        Assert.Equal(V4l2Native.hc_v4l2_struct_size(1), sizeof(V4l2Native.Format));
        Assert.Equal(V4l2Native.hc_v4l2_struct_size(2), sizeof(V4l2Native.Actual));
        Assert.Equal(V4l2Native.hc_v4l2_struct_size(3), sizeof(V4l2Native.Buffer));
    }

    [Fact]
    public void FourCCsRoundTripAndMapToSelectorNames()
    {
        Assert.Equal("MJPG", V4l2Native.FourCCText(V4l2Native.FourCCCode("MJPG")));
        var f = new V4l2Format("YUYV", false, 1920, 1080, [(1, 60), (1, 30)]);
        Assert.Equal("YUY2", f.Descriptor.FourCC);
        Assert.Equal(60, f.Descriptor.MaxFrameRate, 6);
        // 1001/60000 is how drivers say 59.94.
        Assert.Equal(59.94, new V4l2Format("NV12", false, 1280, 720, [(1001, 60000)]).Descriptor.MaxFrameRate, 2);
    }

    [Fact]
    public void ProbingAMissingNodeReportsErrno()
    {
        Assert.Null(V4l2Devices.Probe("/dev/video-does-not-exist", out var error));
        Assert.Equal(-Errno.ENOENT, error);
    }

    [Fact]
    public void ReadsTheUsbIdentityAboveTheInterface()
    {
        // /sys/class/video4linux/video0/device points at the USB interface (1-1:1.0); the USB device is its parent.
        var root = Directory.CreateTempSubdirectory("hc-sysfs").FullName;
        try
        {
            var usb = Path.Combine(root, "devices", "1-1");
            var iface = Path.Combine(usb, "1-1:1.0");
            Directory.CreateDirectory(iface);
            File.WriteAllText(Path.Combine(usb, "idVendor"), "1de1\n");
            File.WriteAllText(Path.Combine(usb, "idProduct"), "f105\n");
            File.WriteAllText(Path.Combine(usb, "product"), "UGREEN-25854\n");
            File.WriteAllText(Path.Combine(usb, "speed"), "480\n");
            var cls = Path.Combine(root, "class");
            Directory.CreateDirectory(Path.Combine(cls, "video0"));
            Directory.CreateSymbolicLink(Path.Combine(cls, "video0", "device"), iface);

            var id = V4l2Devices.ReadUsb("video0", cls);
            Assert.NotNull(id);
            Assert.Equal((0x1DE1, 0xF105), (id.VendorId, id.ProductId));
            Assert.Null(id.Serial);
            Assert.Equal(UsbLink.Usb2, id.Link);
            Assert.Equal("usb:1DE1:F105|name:UGREEN-25854", id.Key);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
