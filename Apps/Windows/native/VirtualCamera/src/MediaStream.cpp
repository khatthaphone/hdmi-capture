#include "MediaSource.h"

#include <algorithm>

namespace vcam
{
    namespace
    {
        struct TypeSpec
        {
            const GUID* subtype;
            UINT32 width;
            UINT32 height;
        };

        // First entry is the default type.
        const TypeSpec TypeSpecs[4] = {
            { &MFVideoFormat_NV12, 1920, 1080 },
            { &MFVideoFormat_NV12, 1280, 720 },
            { &MFVideoFormat_YUY2, 1920, 1080 },
            { &MFVideoFormat_YUY2, 1280, 720 },
        };

        HRESULT CreateVideoType(const TypeSpec& spec, IMFMediaType** out)
        {
            const PixelFormat format = *spec.subtype == MFVideoFormat_NV12 ? PixelFormat::NV12 : PixelFormat::YUY2;
            const UINT32 sampleSize = static_cast<UINT32>(FrameBytes(format, spec.width, spec.height));
            ComPtr<IMFMediaType> type;
            VCAM_RETURN_IF_FAILED(MFCreateMediaType(&type));
            VCAM_RETURN_IF_FAILED(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
            VCAM_RETURN_IF_FAILED(type->SetGUID(MF_MT_SUBTYPE, *spec.subtype));
            VCAM_RETURN_IF_FAILED(MFSetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, spec.width, spec.height));
            VCAM_RETURN_IF_FAILED(MFSetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, FrameRate, 1));
            VCAM_RETURN_IF_FAILED(MFSetAttributeRatio(type.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
            VCAM_RETURN_IF_FAILED(type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
            VCAM_RETURN_IF_FAILED(type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE));
            VCAM_RETURN_IF_FAILED(type->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE));
            VCAM_RETURN_IF_FAILED(type->SetUINT32(MF_MT_SAMPLE_SIZE, sampleSize));
            VCAM_RETURN_IF_FAILED(type->SetUINT32(MF_MT_DEFAULT_STRIDE, static_cast<UINT32>(PackedPitch(format, spec.width))));
            VCAM_RETURN_IF_FAILED(type->SetUINT32(MF_MT_AVG_BITRATE, sampleSize * 8 * FrameRate));
            VCAM_RETURN_IF_FAILED(type->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709));
            VCAM_RETURN_IF_FAILED(type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235));
            VCAM_RETURN_IF_FAILED(type->SetUINT32(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709));
            VCAM_RETURN_IF_FAILED(type->SetUINT32(MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_709));
            *out = type.Detach();
            return S_OK;
        }

        // Locks the sample's first buffer for writing and exposes it as a Frame.
        class BufferLock
        {
        public:
            BufferLock(IMFSample* sample, PixelFormat format, uint32_t width, uint32_t height)
            {
                if (FAILED(sample->GetBufferByIndex(0, &buffer_)))
                    return;
                BYTE* scan0 = nullptr;
                LONG pitch = 0;
                if (SUCCEEDED(buffer_.As(&buffer2d2_)))
                {
                    BYTE* start = nullptr;
                    DWORD length = 0;
                    if (FAILED(buffer2d2_->Lock2DSize(MF2DBuffer_LockFlags_Write, &scan0, &pitch, &start, &length)))
                    {
                        buffer2d2_.Reset();
                        return;
                    }
                    const uint64_t rows = format == PixelFormat::NV12 ? height + height / 2 : height;
                    if (pitch > 0 && length < static_cast<uint64_t>(pitch) * rows)
                        hr_ = E_NOT_SUFFICIENT_BUFFER;
                }
                else if (SUCCEEDED(buffer_.As(&buffer2d_)))
                {
                    if (FAILED(buffer2d_->Lock2D(&scan0, &pitch)))
                    {
                        buffer2d_.Reset();
                        return;
                    }
                }
                else
                {
                    DWORD maxLength = 0;
                    if (FAILED(buffer_->Lock(&scan0, &maxLength, nullptr)))
                        return;
                    locked1d_ = true;
                    pitch = static_cast<LONG>(PackedPitch(format, width));
                    if (maxLength < FrameBytes(format, width, height))
                        hr_ = E_NOT_SUFFICIENT_BUFFER;
                }
                if (pitch <= 0)
                    hr_ = MF_E_UNSUPPORTED_FORMAT; // bottom-up layouts are not used for YUV
                if (hr_ == E_FAIL)
                    hr_ = S_OK;
                frame_ = { format, width, height, scan0, pitch };
            }

            ~BufferLock()
            {
                if (buffer2d2_)
                    buffer2d2_->Unlock2D();
                else if (buffer2d_)
                    buffer2d_->Unlock2D();
                else if (locked1d_)
                {
                    buffer_->Unlock();
                    buffer_->SetCurrentLength(static_cast<DWORD>(FrameBytes(frame_.format, frame_.width, frame_.height)));
                }
            }

            BufferLock(const BufferLock&) = delete;
            BufferLock& operator=(const BufferLock&) = delete;

            HRESULT Result() const { return hr_; }
            const Frame& Get() const { return frame_; }

        private:
            ComPtr<IMFMediaBuffer> buffer_;
            ComPtr<IMF2DBuffer2> buffer2d2_;
            ComPtr<IMF2DBuffer> buffer2d_;
            bool locked1d_ = false;
            HRESULT hr_ = E_FAIL;
            Frame frame_{};
        };
    }

    HRESULT CreateMediaTypes(IMFMediaType* types[4])
    {
        for (int i = 0; i < 4; i++)
        {
            const HRESULT hr = CreateVideoType(TypeSpecs[i], &types[i]);
            if (FAILED(hr))
            {
                for (int j = 0; j < i; j++)
                {
                    types[j]->Release();
                    types[j] = nullptr;
                }
                return hr;
            }
        }
        return S_OK;
    }

    MediaStream::~MediaStream()
    {
        StopPacer();
        if (wake_)
            CloseHandle(wake_);
        if (timer_)
            CloseHandle(timer_);
    }

    HRESULT MediaStream::RuntimeClassInitialize(IMFMediaSource* source, DWORD streamId)
    {
        source_ = source;
        VCAM_RETURN_IF_FAILED(MFCreateAttributes(&attributes_, 4));
        VCAM_RETURN_IF_FAILED(attributes_->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PINNAME_VIDEO_CAPTURE));
        VCAM_RETURN_IF_FAILED(attributes_->SetUINT32(MF_DEVICESTREAM_STREAM_ID, streamId));
        VCAM_RETURN_IF_FAILED(attributes_->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1));
        VCAM_RETURN_IF_FAILED(attributes_->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color));
        VCAM_RETURN_IF_FAILED(MFCreateEventQueue(&queue_));

        IMFMediaType* types[4] = {};
        VCAM_RETURN_IF_FAILED(CreateMediaTypes(types));
        HRESULT hr = MFCreateStreamDescriptor(streamId, 4, types, &descriptor_);
        if (SUCCEEDED(hr))
        {
            ComPtr<IMFMediaTypeHandler> handler;
            hr = descriptor_->GetMediaTypeHandler(&handler);
            if (SUCCEEDED(hr))
                hr = handler->SetCurrentMediaType(types[0]);
        }
        for (IMFMediaType* type : types)
            type->Release();
        VCAM_RETURN_IF_FAILED(hr);

        wake_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        timer_ = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (!timer_) // older builds: a normal timer still works, with coarser pacing
            timer_ = CreateWaitableTimerExW(nullptr, nullptr, 0, TIMER_ALL_ACCESS);
        if (!wake_ || !timer_)
            return HRESULT_FROM_WIN32(GetLastError());
        try
        {
            pacer_ = std::thread([this] { PacerLoop(); });
        }
        catch (...)
        {
            return E_OUTOFMEMORY;
        }
        return S_OK;
    }

    // IMFMediaEventGenerator. The queue is used outside the lock: GetEvent can block.
    STDMETHODIMP MediaStream::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state)
    {
        ComPtr<IMFMediaEventQueue> queue;
        {
            std::lock_guard guard(lock_);
            queue = queue_;
        }
        return queue ? queue->BeginGetEvent(callback, state) : MF_E_SHUTDOWN;
    }

    STDMETHODIMP MediaStream::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event)
    {
        if (!event)
            return E_POINTER;
        *event = nullptr;
        ComPtr<IMFMediaEventQueue> queue;
        {
            std::lock_guard guard(lock_);
            queue = queue_;
        }
        return queue ? queue->EndGetEvent(result, event) : MF_E_SHUTDOWN;
    }

    STDMETHODIMP MediaStream::GetEvent(DWORD flags, IMFMediaEvent** event)
    {
        if (!event)
            return E_POINTER;
        *event = nullptr;
        ComPtr<IMFMediaEventQueue> queue;
        {
            std::lock_guard guard(lock_);
            queue = queue_;
        }
        return queue ? queue->GetEvent(flags, event) : MF_E_SHUTDOWN;
    }

    STDMETHODIMP MediaStream::QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* eventValue)
    {
        std::lock_guard guard(lock_);
        return queue_ ? queue_->QueueEventParamVar(type, extendedType, status, eventValue) : MF_E_SHUTDOWN;
    }

    // IMFMediaStream
    STDMETHODIMP MediaStream::GetMediaSource(IMFMediaSource** source)
    {
        if (!source)
            return E_POINTER;
        *source = nullptr;
        std::lock_guard guard(lock_);
        return source_ ? source_.CopyTo(source) : MF_E_SHUTDOWN;
    }

    STDMETHODIMP MediaStream::GetStreamDescriptor(IMFStreamDescriptor** descriptor)
    {
        if (!descriptor)
            return E_POINTER;
        *descriptor = nullptr;
        std::lock_guard guard(lock_);
        return descriptor_ ? descriptor_.CopyTo(descriptor) : MF_E_SHUTDOWN;
    }

    STDMETHODIMP MediaStream::RequestSample(IUnknown* token)
    {
        {
            std::lock_guard guard(lock_);
            if (!queue_)
                return MF_E_SHUTDOWN;
            if (state_ == MF_STREAM_STATE_STOPPED)
                return MF_E_MEDIA_SOURCE_WRONGSTATE;
            try
            {
                pending_.emplace_back(token);
            }
            catch (...)
            {
                return E_OUTOFMEMORY;
            }
        }
        SetEvent(wake_);
        return S_OK;
    }

    // IMFMediaStream2
    STDMETHODIMP MediaStream::SetStreamState(MF_STREAM_STATE newState)
    {
        HRESULT hr = S_OK;
        {
            std::lock_guard guard(lock_);
            if (!queue_)
                return MF_E_SHUTDOWN;
            if (newState == state_)
                return S_OK;
            switch (newState)
            {
            case MF_STREAM_STATE_RUNNING:
                if (state_ == MF_STREAM_STATE_PAUSED)
                    state_ = MF_STREAM_STATE_RUNNING;
                else
                    hr = StartLocked(nullptr);
                break;
            case MF_STREAM_STATE_PAUSED:
                if (state_ != MF_STREAM_STATE_RUNNING)
                    return MF_E_INVALID_STATE_TRANSITION;
                state_ = MF_STREAM_STATE_PAUSED;
                break;
            case MF_STREAM_STATE_STOPPED:
                hr = StopLocked();
                break;
            default:
                return MF_E_INVALID_STATE_TRANSITION;
            }
        }
        SetEvent(wake_);
        return hr;
    }

    STDMETHODIMP MediaStream::GetStreamState(MF_STREAM_STATE* current)
    {
        if (!current)
            return E_POINTER;
        std::lock_guard guard(lock_);
        *current = state_;
        return S_OK;
    }

    // IKsControl
    STDMETHODIMP MediaStream::KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG* bytesReturned)
    {
        if (bytesReturned)
            *bytesReturned = 0;
        return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }

    STDMETHODIMP MediaStream::KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG* bytesReturned)
    {
        if (bytesReturned)
            *bytesReturned = 0;
        return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }

    STDMETHODIMP MediaStream::KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG* bytesReturned)
    {
        if (bytesReturned)
            *bytesReturned = 0;
        return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }

    // Called by MediaSource
    HRESULT MediaStream::Start(IMFMediaType* type)
    {
        HRESULT hr;
        {
            std::lock_guard guard(lock_);
            hr = StartLocked(type);
        }
        SetEvent(wake_);
        return hr;
    }

    HRESULT MediaStream::Stop()
    {
        HRESULT hr;
        {
            std::lock_guard guard(lock_);
            hr = StopLocked();
        }
        SetEvent(wake_);
        return hr;
    }

    bool MediaStream::IsRunning()
    {
        std::lock_guard guard(lock_);
        return state_ != MF_STREAM_STATE_STOPPED;
    }

    HRESULT MediaStream::SetAllocator(IUnknown* allocator)
    {
        if (!allocator)
            return E_POINTER;
        std::lock_guard guard(lock_);
        if (!queue_)
            return MF_E_SHUTDOWN;
        ComPtr<IMFVideoSampleAllocatorEx> ex;
        VCAM_RETURN_IF_FAILED(allocator->QueryInterface(IID_PPV_ARGS(&ex)));
        if (allocator_ && state_ != MF_STREAM_STATE_STOPPED)
            allocator_->UninitializeSampleAllocator();
        allocator_ = ex;
        if (state_ != MF_STREAM_STATE_STOPPED && type_)
            return allocator_->InitializeSampleAllocator(10, type_.Get());
        return S_OK;
    }

    void MediaStream::Shutdown()
    {
        {
            std::lock_guard guard(lock_);
            if (queue_)
                queue_->Shutdown();
            queue_.Reset();
            pending_.clear();
            state_ = MF_STREAM_STATE_STOPPED;
            if (allocator_)
                allocator_->UninitializeSampleAllocator();
            allocator_.Reset();
            descriptor_.Reset();
            source_.Reset();
            type_.Reset();
        }
        StopPacer();
    }

    HRESULT MediaStream::StartLocked(IMFMediaType* type)
    {
        if (!queue_)
            return MF_E_SHUTDOWN;
        if (type)
            type_ = type;
        if (!type_)
        {
            ComPtr<IMFMediaTypeHandler> handler;
            VCAM_RETURN_IF_FAILED(descriptor_->GetMediaTypeHandler(&handler));
            VCAM_RETURN_IF_FAILED(handler->GetCurrentMediaType(&type_));
        }

        GUID subtype = GUID_NULL;
        UINT32 width = 0, height = 0;
        VCAM_RETURN_IF_FAILED(type_->GetGUID(MF_MT_SUBTYPE, &subtype));
        VCAM_RETURN_IF_FAILED(MFGetAttributeSize(type_.Get(), MF_MT_FRAME_SIZE, &width, &height));
        if (subtype == MFVideoFormat_NV12)
            format_ = PixelFormat::NV12;
        else if (subtype == MFVideoFormat_YUY2)
            format_ = PixelFormat::YUY2;
        else
            return MF_E_INVALIDMEDIATYPE;
        if (width == 0 || height == 0 || (width % 2) != 0 || (height % 2) != 0)
            return MF_E_INVALIDMEDIATYPE;
        width_ = width;
        height_ = height;
        try
        {
            pattern_.Prepare(format_, width_, height_);
        }
        catch (...)
        {
            return E_OUTOFMEMORY;
        }

        // Frame Server hands us its allocator through IMFSampleAllocatorControl; in-process
        // users (vcam selftest) don't, so fall back to our own system-memory allocator.
        if (!allocator_)
            VCAM_RETURN_IF_FAILED(MFCreateVideoSampleAllocatorEx(IID_PPV_ARGS(&allocator_)));
        allocator_->UninitializeSampleAllocator();
        VCAM_RETURN_IF_FAILED(allocator_->InitializeSampleAllocator(10, type_.Get()));

        frameNumber_ = 0;
        nextDue_ = MFGetSystemTime();
        discontinuity_ = true;
        pending_.clear();
        state_ = MF_STREAM_STATE_RUNNING;
        return queue_->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr);
    }

    HRESULT MediaStream::StopLocked()
    {
        if (!queue_)
            return MF_E_SHUTDOWN;
        state_ = MF_STREAM_STATE_STOPPED;
        pending_.clear();
        if (allocator_)
            allocator_->UninitializeSampleAllocator();
        return queue_->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
    }

    bool MediaStream::DeliverLocked(IUnknown* token)
    {
        ComPtr<IMFSample> sample;
        HRESULT hr = allocator_ ? allocator_->AllocateSample(&sample) : MF_E_NOT_INITIALIZED;
        if (hr == MF_E_SAMPLEALLOCATOR_EMPTY)
            return false; // the consumer still holds every sample; try again next slot
        if (SUCCEEDED(hr))
        {
            BufferLock lock(sample.Get(), format_, width_, height_);
            hr = lock.Result();
            if (SUCCEEDED(hr))
                pattern_.Render(lock.Get(), frameNumber_);
        }
        if (SUCCEEDED(hr))
            hr = sample->SetSampleTime(MFGetSystemTime());
        if (SUCCEEDED(hr))
            hr = sample->SetSampleDuration(FrameDuration);
        if (SUCCEEDED(hr))
            hr = sample->SetUINT32(MFSampleExtension_CleanPoint, TRUE);
        if (SUCCEEDED(hr) && discontinuity_)
            hr = sample->SetUINT32(MFSampleExtension_Discontinuity, TRUE);
        if (SUCCEEDED(hr) && token)
            hr = sample->SetUnknown(MFSampleExtension_Token, token);
        if (SUCCEEDED(hr))
            hr = queue_->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.Get());
        if (FAILED(hr))
        {
            queue_->QueueEventParamVar(MEError, GUID_NULL, hr, nullptr);
            return true; // drop this request
        }
        discontinuity_ = false;
        frameNumber_++;
        return true;
    }

    void MediaStream::PacerLoop()
    {
        for (;;)
        {
            bool waitTimer = false;
            {
                std::lock_guard guard(lock_);
                if (exit_)
                    return;
                if (state_ == MF_STREAM_STATE_RUNNING && !pending_.empty() && queue_)
                {
                    const LONGLONG now = MFGetSystemTime();
                    if (now >= nextDue_)
                    {
                        if (DeliverLocked(pending_.front().Get()))
                            pending_.pop_front();
                        nextDue_ += FrameDuration;
                        // After a long stall, restart the schedule instead of bursting to catch up.
                        if (now - nextDue_ > 4 * FrameDuration)
                            nextDue_ = now + FrameDuration;
                        continue;
                    }
                    LARGE_INTEGER due;
                    due.QuadPart = -(nextDue_ - now); // relative, 100 ns units
                    waitTimer = SetWaitableTimer(timer_, &due, 0, nullptr, nullptr, FALSE) != FALSE;
                }
            }
            HANDLE handles[2] = { wake_, timer_ };
            WaitForMultipleObjects(waitTimer ? 2 : 1, handles, FALSE, waitTimer ? INFINITE : 1000);
        }
    }

    void MediaStream::StopPacer()
    {
        {
            std::lock_guard guard(lock_);
            exit_ = true;
        }
        if (wake_)
            SetEvent(wake_);
        if (pacer_.joinable())
        {
            if (pacer_.get_id() == std::this_thread::get_id())
                pacer_.detach(); // last release on our own thread: nothing else touches `this` afterwards
            else
                pacer_.join();
        }
    }
}
