#ifndef RESULT_QUEUE_H
#define RESULT_QUEUE_H

#include <string>
#include <deque>
#include <mutex>

struct QueuedResult {
    std::string json;
    bool is_final;
};

// Thread-safe result queue.
// Recognition thread pushes results; C# polls from the main thread.
// Mutex is acceptable here — push frequency is low (~4/sec) and
// pop is once per Unity frame.
class ResultQueue {
public:
    void Push(std::string json, bool is_final) {
        std::lock_guard<std::mutex> lock(mutex_);
        queue_.push_back({std::move(json), is_final});
    }

    bool Pop(QueuedResult& out) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (queue_.empty())
            return false;
        out = std::move(queue_.front());
        queue_.pop_front();
        return true;
    }

    bool HasResult() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return !queue_.empty();
    }

    void Clear() {
        std::lock_guard<std::mutex> lock(mutex_);
        queue_.clear();
    }

private:
    std::deque<QueuedResult> queue_;
    mutable std::mutex mutex_;
};

#endif // RESULT_QUEUE_H
