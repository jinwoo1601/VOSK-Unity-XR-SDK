#ifndef RING_BUFFER_H
#define RING_BUFFER_H

#include <atomic>
#include <cstdint>
#include <cstring>
#include <algorithm>

// Lock-free single-producer single-consumer ring buffer.
// Producer: AAudio callback thread (high priority).
// Consumer: recognition thread.
//
// On overflow the producer overwrites the oldest unread samples
// and sets the overflow flag. It never blocks.
template <typename T, uint32_t Size = 65536>
class RingBuffer {
    static_assert((Size & (Size - 1)) == 0, "Size must be a power of two");

public:
    RingBuffer() : write_pos_(0), read_pos_(0), overflow_(false) {}

    // Producer: write samples into the buffer.
    // Returns the number of samples actually written (always count).
    // On overflow the producer overwrites oldest data without touching
    // read_pos_ — the consumer detects the lap and skips forward.
    uint32_t Write(const T* data, uint32_t count) {
        uint32_t w = write_pos_.load(std::memory_order_relaxed);

        // Write samples with wrap-around (may overwrite unread data)
        uint32_t pos = w & (Size - 1);
        uint32_t first_chunk = std::min(count, Size - pos);
        std::memcpy(&buffer_[pos], data, first_chunk * sizeof(T));
        if (count > first_chunk) {
            std::memcpy(&buffer_[0], data + first_chunk, (count - first_chunk) * sizeof(T));
        }

        write_pos_.store(w + count, std::memory_order_release);

        // Check if we lapped the reader
        uint32_t r = read_pos_.load(std::memory_order_acquire);
        if ((w + count) - r > Size)
            overflow_.store(true, std::memory_order_release);

        return count;
    }

    // Consumer: read up to max_count samples from the buffer.
    // Returns the number of samples actually read.
    // If the producer has lapped us, snaps read position forward to the
    // oldest valid data.
    uint32_t Read(T* data, uint32_t max_count) {
        uint32_t w = write_pos_.load(std::memory_order_acquire);
        uint32_t r = read_pos_.load(std::memory_order_relaxed);

        // If producer lapped us, snap forward to oldest valid data
        if (w - r > Size) {
            r = w - Size;
            overflow_.store(true, std::memory_order_release);
        }

        uint32_t available = w - r;
        if (available == 0)
            return 0;

        uint32_t to_read = std::min(max_count, available);
        uint32_t pos = r & (Size - 1);
        uint32_t first_chunk = std::min(to_read, Size - pos);
        std::memcpy(data, &buffer_[pos], first_chunk * sizeof(T));
        if (to_read > first_chunk) {
            std::memcpy(data + first_chunk, &buffer_[0], (to_read - first_chunk) * sizeof(T));
        }

        read_pos_.store(r + to_read, std::memory_order_release);
        return to_read;
    }

    // Total capacity in samples (for producers that pace instead of overwrite).
    static constexpr uint32_t kCapacity = Size;

    // Number of samples available for reading.
    uint32_t Available() const {
        uint32_t w = write_pos_.load(std::memory_order_acquire);
        uint32_t r = read_pos_.load(std::memory_order_relaxed);
        return w - r;
    }

    // Check and clear the overflow flag.
    bool CheckOverflow() {
        return overflow_.exchange(false, std::memory_order_acq_rel);
    }

    void Reset() {
        write_pos_.store(0, std::memory_order_relaxed);
        read_pos_.store(0, std::memory_order_relaxed);
        overflow_.store(false, std::memory_order_relaxed);
    }

private:
    T buffer_[Size];
    std::atomic<uint32_t> write_pos_;
    std::atomic<uint32_t> read_pos_;
    std::atomic<bool> overflow_;
};

#endif // RING_BUFFER_H
